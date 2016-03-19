// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Security;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

using CURLcode = Interop.Http.CURLcode;
using CURLINFO = Interop.Http.CURLINFO;

namespace System.Net.Http
{
    internal partial class CurlHandler : HttpMessageHandler
    {
        private static class SslProvider
        {
            private static readonly Interop.Http.SslCtxCallback s_sslCtxCallback = SetSslCtxVerifyCallback;
            private static readonly Interop.Ssl.AppVerifyCallback s_sslVerifyCallback = VerifyCertChain;

            internal static void SetSslOptions(EasyRequest easy, ClientCertificateOption clientCertOption)
            {
                Debug.Assert(clientCertOption == ClientCertificateOption.Automatic || clientCertOption == ClientCertificateOption.Manual);

                // Disable SSLv2/SSLv3, allow TLSv1.*
                easy.SetCurlOption(Interop.Http.CURLoption.CURLOPT_SSLVERSION, (long)Interop.Http.CurlSslVersion.CURL_SSLVERSION_TLSv1);

                X509Certificate2Collection clientCertificates = easy._handler._clientCertificates;
                ClientCertificateProvider certProvider =
                    clientCertOption == ClientCertificateOption.Automatic ? new ClientCertificateProvider(null) : // automatic
                    clientCertificates?.Count > 0 ? new ClientCertificateProvider(clientCertificates) : // manual with certs
                    null; // manual without certs

                IntPtr userPointer = IntPtr.Zero;
                if (certProvider != null)
                {
                    userPointer = GCHandle.ToIntPtr(certProvider._gcHandle);
                    easy.Task.ContinueWith((_, state) => ((IDisposable)state).Dispose(),
                                           certProvider,
                                           CancellationToken.None,
                                           TaskContinuationOptions.ExecuteSynchronously,
                                           TaskScheduler.Default);
                }

                CURLcode answer = easy.SetSslCtxCallback(s_sslCtxCallback, userPointer);
                switch (answer)
                {
                    case CURLcode.CURLE_OK:
                        break;

                    // Curl 7.38 and prior
                    case CURLcode.CURLE_UNKNOWN_OPTION:
                    // Curl 7.39 and later
                    case CURLcode.CURLE_NOT_BUILT_IN:
                        EventSourceTrace("CURLOPT_SSL_CTX_FUNCTION not supported.");
                        if (certProvider != null || easy._handler.ServerCertificateValidationCallback != null)
                        {
                            throw new PlatformNotSupportedException(SR.net_http_unix_invalid_certcallback_option);
                        }
                        break;

                    default:
                        ThrowIfCURLEError(answer);
                        break;
                }
            }

            private static CURLcode SetSslCtxVerifyCallback(
                IntPtr curl,
                IntPtr sslCtx,
                IntPtr userPointer)
            {
                using (SafeSslContextHandle ctx = new SafeSslContextHandle(sslCtx, ownsHandle: false))
                {
                    Interop.Ssl.SslCtxSetCertVerifyCallback(ctx, s_sslVerifyCallback, curl);

                    if (userPointer == IntPtr.Zero)
                    {
                        EventSourceTrace("Not using client certificate callback");
                    }
                    else
                    {
                        ClientCertificateProvider provider = null;
                        try
                        {
                            GCHandle handle = GCHandle.FromIntPtr(userPointer);
                            provider = (ClientCertificateProvider)handle.Target;
                        }
                        catch (InvalidCastException)
                        {
                            Debug.Fail("ClientCertificateProvider wasn't the GCHandle's Target");
                            return CURLcode.CURLE_ABORTED_BY_CALLBACK;
                        }
                        catch (InvalidOperationException)
                        {
                            Debug.Fail("Invalid GCHandle in CurlSslCallback");
                            return CURLcode.CURLE_ABORTED_BY_CALLBACK;
                        }

                        Debug.Assert(provider != null, "Expected non-null sslCallback in curlCallBack");
                        Interop.Ssl.SslCtxSetClientCertCallback(ctx, provider._callback);
                    }
                }
                return CURLcode.CURLE_OK;
            }

            private static void AddChannelBindingToken(X509Certificate2 certificate, IntPtr curlPtr)
            {
                EasyRequest easy;
                if (TryGetEasyRequest(curlPtr, out easy) && easy._requestContentStream != null)
                {
                    easy._requestContentStream.SetChannelBindingToken(certificate);
                }
            }

            private static bool TryGetEasyRequest(IntPtr curlPtr, out EasyRequest easy)
            {
                Debug.Assert(curlPtr != IntPtr.Zero, "curlPtr is not null");
                IntPtr gcHandlePtr;
                CURLcode getInfoResult = Interop.Http.EasyGetInfoPointer(curlPtr, CURLINFO.CURLINFO_PRIVATE, out gcHandlePtr);
                Debug.Assert(getInfoResult == CURLcode.CURLE_OK, "Failed to get info on a completing easy handle");
                if (getInfoResult == CURLcode.CURLE_OK)
                {
                    GCHandle handle = GCHandle.FromIntPtr(gcHandlePtr);
                    easy = handle.Target as EasyRequest;
                    Debug.Assert(easy != null, "Expected non-null EasyRequest in GCHandle");
                    return easy != null;
                }

                easy = null;
                return false;
            }

            private static int VerifyCertChain(IntPtr storeCtxPtr, IntPtr curlPtr)
            {
                EasyRequest easy;
                if (!TryGetEasyRequest(curlPtr, out easy))
                {
                    EventSourceTrace("Could not find associated easy request: {0}", curlPtr);
                    return 0;
                }

                using (SafeX509StoreCtxHandle storeCtx = new SafeX509StoreCtxHandle(storeCtxPtr, ownsHandle: false))
                {
                    // We need to respect the user's server validation callback if there is one.  If there isn't one,
                    // we can start by first trying to use OpenSSL's verification, though only if CRL checking is disabled,
                    // as OpenSSL doesn't do that.
                    if (easy._handler.ServerCertificateValidationCallback == null &&
                        !easy._handler.CheckCertificateRevocationList)
                    {
                        // Start by using the default verification provided directly by OpenSSL.
                        // If it succeeds in verifying the cert chain, we're done. Employing this instead of 
                        // our custom implementation will need to be revisited if we ever decide to introduce a 
                        // "disallowed" store that enables users to "untrust" certs the system trusts.
                        int sslResult = Interop.Crypto.X509VerifyCert(storeCtx);
                        if (sslResult == 1)
                        {
                            return 1;
                        }

                        // X509_verify_cert can return < 0 in the case of programmer error
                        Debug.Assert(sslResult == 0, "Unexpected error from X509_verify_cert: " + sslResult);
                    }

                    // Either OpenSSL verification failed, or there was a server validation callback.
                    // Either way, fall back to manual and more expensive verification that includes 
                    // checking the user's certs (not just the system store ones as OpenSSL does).
                    List<X509Certificate2> otherCerts;
                    bool success;
                    using (X509Chain chain = new X509Chain())
                    {
                        chain.ChainPolicy.RevocationMode = easy._handler.CheckCertificateRevocationList ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
                        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

                        IntPtr leafCertPtr = Interop.Crypto.X509StoreCtxGetTargetCert(storeCtx);

                        if (IntPtr.Zero == leafCertPtr)
                        {
                            Debug.Fail("Invalid target certificate");
                            return -1;
                        }

                        using (SafeSharedX509StackHandle extraStack = Interop.Crypto.X509StoreCtxGetSharedUntrusted(storeCtx))
                        {
                            int extraSize = extraStack.IsInvalid ? 0 : Interop.Crypto.GetX509StackFieldCount(extraStack);
                            otherCerts = new List<X509Certificate2>(extraSize);

                            for (int i = 0; i < extraSize; i++)
                            {
                                IntPtr certPtr = Interop.Crypto.GetX509StackField(extraStack, i);

                                if (certPtr != IntPtr.Zero)
                                {
                                    X509Certificate2 cert = new X509Certificate2(certPtr);
                                    otherCerts.Add(cert);
                                    chain.ChainPolicy.ExtraStore.Add(cert);
                                }
                            }
                        }

                        using (X509Certificate2 leafCert = new X509Certificate2(leafCertPtr))
                        {
                            var serverCallback = easy._handler._serverCertificateValidationCallback;
                            if (serverCallback == null)
                            {
                                SslPolicyErrors errors = CertificateValidation.BuildChainAndVerifyProperties(
                                    chain, leafCert, checkCertName: false, hostName: null);
                                success = errors == SslPolicyErrors.None;
                            }
                            else
                            {
                                SslPolicyErrors errors = CertificateValidation.BuildChainAndVerifyProperties(
                                    chain, leafCert, checkCertName: true, hostName: easy._requestMessage.RequestUri.Host);
                                try
                                {
                                    success = serverCallback(easy._requestMessage, leafCert, chain, errors);
                                }
                                catch (Exception exc)
                                {
                                    EventSourceTrace("Server validation callback threw exception: {0}", exc);
                                    easy.FailRequest(exc);
                                    success = false;
                                }
                            }

                            AddChannelBindingToken(leafCert, curlPtr);
                        }
                    }

                    foreach (X509Certificate2 otherCert in otherCerts)
                    {
                        otherCert.Dispose();
                    }

                    return success ? 1 : 0;
                }
            }
        }
    }
}
