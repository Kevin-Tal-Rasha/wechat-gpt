using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using wechat_gpt.WeChatLib;
using static wechat_gpt.Controllers.WechatCmdHandler;

namespace wechat_gpt.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class ChatGPTController : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Test()
        {
            return Ok("Test: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }

        private readonly string WECHAT_CORPID;
        private readonly string WECHAT_CORPSECRET;
        private readonly string WECHAT_TOKEN;
        private readonly string WECHAT_ENCODING_AESKEY;
        private readonly string WECHAT_AGENT_ID;
        private readonly string GPT_URL;
        private readonly string GPT_TOKEN;
        private readonly int CONVERSATION_COUNT;

        private readonly WXBizMsgCrypt _wxBizMsgCrypt;

        //GPT API请求中状态记录
        private static ConcurrentDictionary<string, DateTime> _fetchingGPTDict = new ConcurrentDictionary<string, DateTime>();
        //对话上下文记录
        private static ConcurrentDictionary<string, List<(DateTime, string)>> _previousConversationDict = new ConcurrentDictionary<string, List<(DateTime, string)>>();

        public ChatGPTController(IConfiguration config)
        {
            WECHAT_CORPID = config.GetValue<string>("WECHAT_CORPID");
            WECHAT_CORPSECRET = config.GetValue<string>("WECHAT_CORPSECRET");
            WECHAT_TOKEN = config.GetValue<string>("WECHAT_TOKEN");
            WECHAT_ENCODING_AESKEY = config.GetValue<string>("WECHAT_ENCODING_AESKEY");
            WECHAT_AGENT_ID = config.GetValue<string>("WECHAT_AGENT_ID");
            GPT_URL = config.GetValue<string>("GPT_URL");
            GPT_TOKEN = config.GetValue<string>("GPT_TOKEN");
            CONVERSATION_COUNT = config.GetValue<int>("CONVERSATION_COUNT");

            _wxBizMsgCrypt = new WXBizMsgCrypt(WECHAT_TOKEN, WECHAT_ENCODING_AESKEY, WECHAT_CORPID);
        }

        private async Task<IActionResult> verifyURL(HttpRequest req)
        {
            string msg_signature = req.Query["msg_signature"];
            string timestamp = req.Query["timestamp"];
            string nonce = req.Query["nonce"];
            string echostr = req.Query["echostr"];
            string result = "";
            int ret = _wxBizMsgCrypt.VerifyURL(msg_signature, timestamp, nonce, echostr, ref result);
            if (ret == 0)
                return Ok(result);
            else
                return BadRequest("ERR: VerifyURL fail, ret: " + ret);
        }

        #region 第三方应用相关action，这里用不到
        [HttpGet]
        public async Task<IActionResult> VerifyURL()
        {
            return await verifyURL(Request);
        }

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> RefreshSuiteTicket()
        {
            if (Request.Method == "POST")
            {
                return Ok("success");
            }
            else
                return await verifyURL(Request);
        }
        #endregion

        [HttpGet]
        [HttpPost]
        public async Task<IActionResult> HandleRequest()
        {
            try
            {
                if (Request.Method == "POST")
                {
                    string msg_signature = Request.Query["msg_signature"];
                    string timestamp = Request.Query["timestamp"];
                    string nonce = Request.Query["nonce"];
                    var reqData = await new StreamReader(Request.Body).ReadToEndAsync();

                    string sMsg = "";
                    int ret = _wxBizMsgCrypt.DecryptMsg(msg_signature, timestamp, nonce, reqData, ref sMsg);
                    if (ret != 0)
                    {
                        System.Console.WriteLine("ERR: HandleRequest Decrypt Fail, ret: " + ret);
                        return BadRequest("ERR: HandleRequest Decrypt fail, ret: " + ret);
                    }

                    XmlDocument doc = new XmlDocument();
                    doc.LoadXml(sMsg);
                    XmlNode root = doc.FirstChild;
                    string content = root["Content"].InnerText;
                    string user = root["FromUserName"].InnerText;
                    string msgId = root["MsgId"].InnerText;

                    string resContent = WechatReplies.ThinkingReply;
                    bool continueGPT = true;

                    //测试企业微信服务是否正常，主要是信任IP经常变需要重设
                    (string accessToken, string errmsg) = await testWechatIpTrusted();
                    if (!string.IsNullOrEmpty(errmsg))
                    {
                        continueGPT = false;
                        resContent = $"企业微信服务请求异常：{errmsg}";
                    }

                    //来自企业微信的命令
                    WechatCommand? cmd = WechatCmdHandler.GetCmd(content);
                    if (cmd != null)
                    {
                        switch (cmd)
                        {
                            case WechatCommand.ChangeSubject:
                                _previousConversationDict.Clear();
                                resContent = WechatReplies.ChangeSubjectReply;
                                continueGPT = false;
                                break;
                        }
                    }

                    string sRespData = "<xml>"
                                     + $"<ToUserName><![CDATA[{root["ToUserName"].InnerText}]]></ToUserName>"
                                     + $"<FromUserName><![CDATA[{user}]]></FromUserName>"
                                     + $"<CreateTime>{DateTime.Now.Ticks}</CreateTime>"
                                     + "<MsgType><![CDATA[text]]></MsgType>"
                                     + $"<Content><![CDATA[{resContent}]]></Content>"
                                     + $"<MsgId>{BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0)}</MsgId>"
                                     + $"<AgentID>{WECHAT_AGENT_ID}</AgentID>"
                                     + "</xml>";
                    string sEncryptMsg = ""; //xml格式的密文
                    ret = _wxBizMsgCrypt.EncryptMsg(sRespData, timestamp, nonce, ref sEncryptMsg);
                    if (ret != 0)
                    {
                        System.Console.WriteLine("ERR: HandleRequest EncryptMsg Fail, ret: " + ret);
                        return BadRequest("ERR: HandleRequest EncryptMsg fail, ret: " + ret);
                    }

                    #region 获取GPT答复
                    if (continueGPT)
                    {
                        //考虑企业微信重发功能，相同msgId不重复进行GPT API请求
                        string key = $"{user}|{msgId}";
                        if (_fetchingGPTDict.TryAdd(key, DateTime.Now))
                        {
                            //获取对话上下文
                            _previousConversationDict.TryGetValue(user, out List<(DateTime, string)>? previous);
                            previous = previous ?? new List<(DateTime, string)>();
                            var prevContents = previous.Where(p =>
                            {
                                return true;
                                //只保留10分钟内的对话上下文，暂时弃用
                                var (time, content) = p;
                                return (DateTime.Now - time).TotalMilliseconds < 10 * 60 * 1000;
                            }).Select(p => p.Item2);

                            //调用GPT API
                            fetchGPTMessage(content, prevContents.ToArray()).ContinueWith(m =>
                            {
                                string gptRes = m.Result;
                                //推送企业微信消息
                                sendMsgToWechat(user, $"**{content}**\n>{gptRes.Replace("\n", "\n>")}", accessToken).ContinueWith(_ =>
                                {
                                    _fetchingGPTDict.TryRemove(key, out var v);

                                    //记录对话上下文
                                    _previousConversationDict.TryAdd(user, previous);
                                    previous.Add((DateTime.Now, content));
                                    previous.Add((DateTime.Now, gptRes));
                                    if (previous.Count > CONVERSATION_COUNT + 1) previous.RemoveRange(0, 2);
                                });
                            });
                        }
                    }
                    #endregion

                    return Ok(sEncryptMsg);
                }
                else
                    return await verifyURL(Request);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return BadRequest(ex);
            }
        }

        [HttpGet]
        public async Task<IActionResult> TestGPT(string msg)
        {
            return Ok(await fetchGPTMessage(msg));
        }

        private async Task<string> fetchGPTMessage(string text, string[] previousConversation = null)
        {
            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", GPT_TOKEN);

                    if (previousConversation != null)
                        text = String.Join("\n\n", previousConversation.Concat(new string[] { text }));

                    var requestData = new
                    {
                        model = "gpt-3.5-turbo",
                        temperature = 0.7,
                        messages = new ArrayList() {
                            new {
                                role="user",
                                content=text
                            }
                        },
                    };

                    string jsonRequestData = JsonConvert.SerializeObject(requestData);
                    HttpContent content = new StringContent(jsonRequestData, Encoding.UTF8, "application/json");

                    HttpResponseMessage response = await client.PostAsync(GPT_URL, content);
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(jsonResponse);

                    if (result.error != null)
                        throw new Exception(result.error.message);
                    else
                    {
                        string gptRes = result.choices[0].message.content;
                        return gptRes.TrimStart('\n');
                    }
                }
            }
            catch (Exception ex)
            {
                return $"GPT 服务连接失败：{ex.Message}";
            }
        }

        [HttpGet]
        public async Task TestSendMsgToWechat(string msg)
        {
            sendMsgToWechat("@all", msg);
        }

        private async Task sendMsgToWechat(string user, string msg, string accessToken = null)
        {
            if (string.IsNullOrEmpty(accessToken))
                accessToken = await getWechatAccessToken(WECHAT_CORPID, WECHAT_CORPSECRET);
            string url = $"https://qyapi.weixin.qq.com/cgi-bin/message/send?access_token={accessToken}";

            string json = @"
{
    ""touser"": """ + user + @""",
    ""msgtype"": ""markdown"",
    ""agentid"": " + WECHAT_AGENT_ID + @",
    ""markdown"": {
        ""content"": """ + msg + @"""
    },
    ""safe"":0
}";

            using (var httpClient = new HttpClient())
            {
                var content = new StringContent(json, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(url, content);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to push message. {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                }
                var res = await response.Content.ReadAsStringAsync();
                dynamic resContent = JsonConvert.DeserializeObject(res);
                if (resContent.errcode != 0)
                    Console.WriteLine($"{resContent.errmsg}");
            }
        }

        private async Task<(string, string)> testWechatIpTrusted()
        {
            string access_token = await getWechatAccessToken(WECHAT_CORPID, WECHAT_CORPSECRET);
            string url = $"https://qyapi.weixin.qq.com/cgi-bin/get_api_domain_ip?access_token={access_token}";

            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Failed to push message. {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                }
                var res = await response.Content.ReadAsStringAsync();
                dynamic resContent = JsonConvert.DeserializeObject(res);
                string errmsg = "";
                if (resContent.errcode != 0)
                    errmsg = resContent.errmsg;

                return (access_token, errmsg.Replace("[", "").Replace("]", ""));
            }
        }

        private async Task<string> getWechatAccessToken(string corpid, string corpsecret)
        {
            using (var httpClient = new HttpClient())
            {
                var response = await httpClient.GetAsync($"https://qyapi.weixin.qq.com/cgi-bin/gettoken?corpid={corpid}&corpsecret={corpsecret}");

                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    var obj = Newtonsoft.Json.JsonConvert.DeserializeObject<dynamic>(json);

                    return obj.access_token;
                }
                else
                {
                    throw new Exception($"Failed to get access token. {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
                }
            }
        }

    }
}
