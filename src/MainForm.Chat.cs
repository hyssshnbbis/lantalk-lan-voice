// MainForm.Chat.cs —— MainForm 的 聊天 部分（partial class）
//
// 把这块单独拆出来，是为了以后改聊天/设置时不用在 1500 行里翻。
// 字段、构建、DPI 缩放仍在 MainForm.cs。
using System;

namespace LanTalk
{
    internal sealed partial class MainForm
    {
        // ==================== 聊天 ====================

        /// <summary>启动时把最近的聊天记录读回界面。</summary>
        private void LoadChatHistory()
        {
            if (_cardChat == null) return;
            System.Collections.Generic.List<ChatMsg> msgs = ChatHistory.LoadTailMsgs(200);
            _cardChat.AddRange(msgs);
            if (msgs.Count > 0) Log.Write("已载入最近 " + msgs.Count + " 条聊天记录。");
        }

        /// <summary>自己发出一条：会议室优先，其次一对一通话中。</summary>
        private void OnChatSend(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            bool sent = false;
            try
            {
                if (_conference != null && _conference.Joined) sent = _conference.SendChat(text);
                else if (_session != null && _session.State == CallState.InCall) sent = _session.SendChat(text);
            }
            catch (Exception ex) { Log.Write("发送聊天失败：" + ex.Message); }

            DateTime now = DateTime.Now;
            _cardChat.Add("我", text, true, now);
            ChatHistory.Append(now, "我", text, true);

            if (!sent) Log.Write("聊天没发出去：当前既不在会议室，也没有接通的一对一通话。");
        }

        /// <summary>自己发一张图（剪贴板粘贴触发）。</summary>
        private void OnChatSendImage(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            int sent = 0;
            try
            {
                if (_conference != null && _conference.Joined) sent = _conference.SendImage(jpeg);
                else if (_session != null && _session.State == CallState.InCall) sent = _session.SendImage(jpeg);
            }
            catch (Exception ex) { Log.Write("发送图片失败：" + ex.Message); }

            DateTime now = DateTime.Now;
            _cardChat.AddImage("我", jpeg, true, now);
            if (sent <= 0) Log.Write("图片没发出去：当前既不在会议室，也没有接通的一对一通话。");
        }

        /// <summary>收到对方发来的图片。</summary>
        private void OnChatImageReceived(string who, byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            _cardChat.AddImage(who, jpeg, false, DateTime.Now);
            Log.Write("收到图片 " + (jpeg.Length / 1024) + " KB，来自 " + who);
        }

        private void OnChatReceived(string who, string text)
        {
            DateTime now = DateTime.Now;
            if (_cardChat != null) _cardChat.Add(who, text, false, now);
            ChatHistory.Append(now, who, text, false);
        }

    }
}
