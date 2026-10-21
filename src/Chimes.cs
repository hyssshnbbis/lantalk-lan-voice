// Chimes.cs —— 进房 / 成员进出 的提示音（纯代码合成，不依赖任何音频文件）
//
// 为什么用程序合成而不是 wav 资源：
//   · 项目坚持零第三方依赖、单文件分发，多一个 wav 就多一份资源要打包；
//   · 合成可以精确控制包络，做到没有爆音（起音 6ms、末尾 20ms 淡出）；
//   · 想调音高/音长只需改几个数字。
//
// 三个音的设计意图：
//   进房（自己）  —— 上行两音，稍响，明确告诉你"进来了"
//   成员加入      —— 单音，轻，不打断你
//   成员离开      —— 下行两音，更轻更短
using System;

namespace LanTalk
{
    internal enum ChimeKind
    {
        JoinRoom = 0,      // 自己进入房间
        MemberJoin = 1,    // 别人进入（或被我方发现）
        MemberLeave = 2    // 别人离开
    }

    internal static class Chimes
    {
        // 音高（十二平均律，A4=440）
        private const double C5 = 523.25;
        private const double E5 = 659.25;
        private const double G5 = 783.99;
        private const double A5 = 880.00;

        public static short[] Make(ChimeKind kind, int rate)
        {
            if (rate <= 0) return new short[0];
            switch (kind)
            {
                case ChimeKind.JoinRoom:
                    // 上行 C5 → G5
                    return Concat(Tone(rate, C5, 100.0, 0.30), Tone(rate, G5, 170.0, 0.28));
                case ChimeKind.MemberJoin:
                    // 单音 A5
                    return Tone(rate, A5, 140.0, 0.20);
                case ChimeKind.MemberLeave:
                    // 下行 A5 → E5，更轻
                    return Concat(Tone(rate, A5, 90.0, 0.16), Tone(rate, E5, 140.0, 0.15));
            }
            return new short[0];
        }

        /// <summary>合成一个带包络的单音。</summary>
        private static short[] Tone(int rate, double freq, double ms, double amp)
        {
            int n = (int)Math.Round(rate * ms / 1000.0);
            if (n < 4) n = 4;
            short[] s = new short[n];

            const double attack = 0.006;     // 起音，避免第一个采样从 0 突然跳到峰值
            const double fade = 0.020;       // 末尾淡出，避免截断产生"咔"声
            double total = n / (double)rate;

            for (int i = 0; i < n; i++)
            {
                double t = i / (double)rate;

                double env;
                if (t < attack) env = t / attack;
                else env = Math.Exp(-(t - attack) * 9.0);

                double remain = total - t;
                if (remain < fade) env *= remain / fade;
                if (env < 0) env = 0;

                // 加一点二次谐波，听起来更圆润、不刺耳
                double v = Math.Sin(2.0 * Math.PI * freq * t) * 0.85
                         + Math.Sin(4.0 * Math.PI * freq * t) * 0.15;

                double outv = v * env * amp * 32767.0;
                if (outv > 32767) outv = 32767;
                else if (outv < -32768) outv = -32768;
                s[i] = (short)outv;
            }
            return s;
        }

        private static short[] Concat(short[] a, short[] b)
        {
            short[] r = new short[a.Length + b.Length];
            Array.Copy(a, 0, r, 0, a.Length);
            Array.Copy(b, 0, r, a.Length, b.Length);
            return r;
        }
    }
}
