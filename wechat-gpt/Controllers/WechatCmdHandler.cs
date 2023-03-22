using System.Collections.Concurrent;

namespace wechat_gpt.Controllers
{
    public class WechatCmdHandler
    {
        public enum WechatCommand
        {
            ChangeSubject
        }

        private static Dictionary<WechatCommand, string[]> _dict = new Dictionary<WechatCommand, string[]> {
            { WechatCommand.ChangeSubject, new string[]{
                                                    "换个话题",
                                                    "换个话题吧",
                                                    "不聊这个了",
                                                    "聊点别的" } }
        };

        public static WechatCommand? GetCmd(string msg)
        {
            foreach (var item in _dict)
            {
                if (item.Value.Contains(msg))
                    return item.Key;
            }

            return null;
        }

    }
}
