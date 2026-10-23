// MainForm.Chat.cs —— MainForm 的 聊天 部分（partial class）
//
// 把这块单独拆出来，是为了以后改聊天/设置时不用在 1500 行里翻。
// 字段、构建、DPI 缩放仍在 MainForm.cs。
using System;
using System.Windows.Forms;

namespace LanTalk
{
    internal sealed partial class MainForm
    {
        // ==================== 聊天 ====================

        /// <summary>把消息流的「删除这条 / 清空聊天记录」接上本地记录。</summary>
        private void BindChatHistoryActions()
        {
            if (_cardChat == null) return;
            _cardChat.View.MsgDeleted += OnChatMsgDeleted;
            _cardChat.View.ClearRequested += OnChatClearRequested;
        }

        /// <summary>删掉一条消息后同步本地记录（图片消息本来就不落盘，只从界面移除）。</summary>
        private void OnChatMsgDeleted(ChatMsg m)
        {
            if (m == null) return;
            if (m.Image != null)
            {
                Log.Write("已移除一条图片消息（图片不进本地记录）。");
                return;
            }
            bool ok = ChatHistory.Delete(m);
            if (!ok) Log.Write("本地记录里没找到这一条，已只从界面移除。");
        }

        /// <summary>清空本地聊天记录：删 chat.log 并清空界面。</summary>
        private void OnChatClearRequested()
        {
            if (_cardChat == null) return;
            // 这里的 DialogResult 要写全名：MainForm 自己有个同名属性，不加限定会被解析成属性
            System.Windows.Forms.DialogResult r = MessageBox.Show(this,
                "确定清除本机的聊天记录吗？\r\n\r\n会删掉 " + ChatHistory.FilePath + "\r\n" +
                "只影响本机，别人那边的记录不受影响。这个操作不能撤销。",
                "清除聊天记录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (r != System.Windows.Forms.DialogResult.OK) return;

            ChatHistory.Clear();
            _cardChat.Clear();
            Log.Write("已清除本地聊天记录。");
        }

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
            }
            catch (Exception ex) { Log.Write("发送聊天失败：" + ex.Message); }

            DateTime now = DateTime.Now;
            _cardChat.Add("我", text, true, now);
            ChatHistory.Append(now, "我", text, true);

            if (!sent) Log.Write("聊天没发出去：还没进会议。");
        }

        /// <summary>自己发一张图（剪贴板粘贴触发）。</summary>
        private void OnChatSendImage(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0) return;
            int sent = 0;
            try
            {
                if (_conference != null && _conference.Joined) sent = _conference.SendImage(jpeg);
            }
            catch (Exception ex) { Log.Write("发送图片失败：" + ex.Message); }

            DateTime now = DateTime.Now;
            _cardChat.AddImage("我", jpeg, true, now);
            if (sent <= 0) Log.Write("图片没发出去：还没进会议。");
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
