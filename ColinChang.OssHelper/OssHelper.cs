using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Aliyun.Acs.Core;
using Aliyun.Acs.Core.Auth.Sts;
using Aliyun.Acs.Core.Profile;
using Aliyun.OSS;
using Newtonsoft.Json;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;

namespace ColinChang.OssHelper
{
    public class OssHelper : IOssHelper
    {
        private readonly OssHelperOptions _options;
        private readonly IOss _oss;
        private readonly HttpClient _httpClient;

        public OssHelper(IOptions<OssHelperOptions> options, IOss oss, HttpClient httpClient)
        {
            _options = options.Value;
            _oss = oss;
            _httpClient = httpClient;
        }

        public async Task<AssumeRoleResponse.AssumeRole_Credentials> GetStsAsync()
        {
            return await Task.Run(() =>
            {
                var profile = DefaultProfile.GetProfile(_options.StsOptions.RegionId, _options.StsOptions.AccessKeyId,
                    _options.StsOptions.AccessKeySecret);
                profile.AddEndpoint(_options.StsOptions.RegionId, _options.StsOptions.RegionId,
                    _options.StsOptions.Product,
                    _options.StsOptions.EndPoint);
                var client = new DefaultAcsClient(profile);
                var request = new AssumeRoleRequest
                {
                    AcceptFormat = Aliyun.Acs.Core.Http.FormatType.JSON,
                    RoleArn = _options.StsOptions.RoleArn,
                    RoleSessionName = _options.StsOptions.RoleSessionName,
                    DurationSeconds = _options.StsOptions.Expiration
                };
                return client.GetAcsResponse(request)?.Credentials;
            });
        }

        public async Task<dynamic> GetPolicyAsync(ObjectType objectType)
        {
            return await Task.Run(() =>
            {
                var expireTime = DateTime.UtcNow.AddSeconds(_options.PolicyOptions.ExpireTime);
                var config = new
                {
                    //OSS 大小写敏感，切勿修改
                    expiration = expireTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.CurrentCulture),
                    conditions = new List<IList<object>> {new List<object>()}
                };
                config.conditions[0].Add("content-length-range");
                config.conditions[0].Add(0);
                config.conditions[0].Add(1048576000);
                config.conditions.Add(new List<object>());
                config.conditions[1].Add("starts-with");
                config.conditions[1].Add("$key");
                var dir = _options[objectType].UploadDir;

                config.conditions[1].Add(dir);

                var policy = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(config)));

                using var algorithm = new HMACSHA1
                    {Key = Encoding.UTF8.GetBytes(_options.PolicyOptions.AccessKeySecret)};
                var signature = Convert.ToBase64String(
                    algorithm.ComputeHash(Encoding.UTF8.GetBytes(policy)));

                var callback = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new
                {
                    CallbackUrl = _options.PolicyOptions.CallbackUrl,
                    CallbackBody =
                        "bucket=${bucket}&filename=${object}&size=${size}&mimeType=${mimeType}&height=${imageInfo.height}&width=${imageInfo.width}",
                    CallbackBodyType = "application/x-www-form-urlencoded"
                })));
                return new
                {
                    AccessId = _options.PolicyOptions.AccessKeyId,
                    Host = _options.PolicyOptions.Host,
                    Policy = policy,
                    Signature = signature,
                    Expire = ((expireTime.Ticks - 621355968000000000) / 10000000L)
                        .ToString(CultureInfo.InvariantCulture),
                    Callback = callback,
                    Dir = dir
                };
            });
        }

        public async Task<OssObject> CallbackAsync(HttpRequest request)
        {
            if (!await request.VerifyOssSignatureAsync(_options.StsOptions.PublicKeyIssuers, _httpClient))
                // throw new OssException("invalid signature");
                return null;

            var obj = request.ExtractOssObject();
            obj.Verify(_oss, _options[obj.MimeType]);
            return await Task.FromResult(obj);
        }
    }
}