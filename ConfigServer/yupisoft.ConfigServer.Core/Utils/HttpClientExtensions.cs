﻿using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace yupisoft.ConfigServer.Core.Utils
{
    public static class HttpClientExtensions
    {
        public static async Task<HttpResponseMessage> PostAsync(this HttpClient client, string requestUri, HttpContent content, string APPId, string APIKey)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, requestUri);
            request.Content = content;
            return await client.SendAsync(request, CancellationToken.None, APPId, APIKey);
        }

        public static async Task<HttpResponseMessage> GetAsync(this HttpClient client, string requestUri, HttpContent content, string APPId, string APIKey)
        {
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Content = content;
            return await client.SendAsync(request, CancellationToken.None, APPId, APIKey);
        }

        public static async Task<HttpResponseMessage> SendAsync(this HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken, string APPId, string APIKey)
        {
            HttpResponseMessage response = null;
            string requestContentBase64String = string.Empty;

            string requestUri = System.Net.WebUtility.UrlEncode(request.RequestUri.AbsoluteUri).ToLower();

            string requestHttpMethod = request.Method.Method;


            //Calculate UNIX time
            DateTime epochStart = new DateTime(1970, 01, 01, 0, 0, 0, 0, DateTimeKind.Utc);
            TimeSpan timeSpan = DateTime.UtcNow - epochStart;
            string requestTimeStamp = Convert.ToUInt64(timeSpan.TotalSeconds).ToString();

            //create random nonce for each request
            string nonce = Guid.NewGuid().ToString("N");

            //Checking if the request contains body, usually will be null wiht HTTP GET and DELETE
            if (request.Content != null)
            {
                byte[] content = await request.Content.ReadAsByteArrayAsync();
                MD5 md5 = MD5.Create();
                //Hashing the request body, any change in request body will result in different hash, we'll incure message integrity
                byte[] requestContentHash = md5.ComputeHash(content);
                requestContentBase64String = Convert.ToBase64String(requestContentHash);
            }

            //Creating the raw signature string
            string data = String.Format("{0}{1}{2}{3}{4}{5}", APPId, requestHttpMethod, requestUri, requestTimeStamp, nonce, requestContentBase64String);

            var secretKeyBytes = Convert.FromBase64String(APIKey);
            byte[] signature = Encoding.UTF8.GetBytes(data);
            using (HMACSHA256 hmac = new HMACSHA256(secretKeyBytes))
            {
                byte[] signatureBytes = hmac.ComputeHash(signature);
                string requestSignatureBase64String = Convert.ToBase64String(signatureBytes);
                //Setting the values in the Authorization header using custom scheme (amx)
                request.Headers.Authorization = new AuthenticationHeaderValue("hmac", string.Format("{0}:{1}:{2}:{3}", APPId, requestSignatureBase64String, nonce, requestTimeStamp));
            }

            response = await client.SendAsync(request, cancellationToken);

            return response;
        }
    }
}
