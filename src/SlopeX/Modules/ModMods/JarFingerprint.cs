using System;
using System.IO;
using SlopeX.Modules.ModBase;

namespace SlopeX.Modules.ModMods
{
    /// <summary>
    /// CurseForge 的文件指纹。
    ///
    /// **不是**对 jar 里的条目名做哈希，而是把**整个文件的字节流**喂给 MurmurHash2（seed = 1），
    /// 只是喂之前先剔除空白字节 <c>0x09 / 0x0A / 0x0D / 0x20</c>，长度也按剔除后算。
    /// 这一点很容易搞错：按"排序后的条目名拼接"去算，CF 一个都认不出来（2026-11-04 实测 0/95）。
    ///
    /// 剔除空白看起来莫名其妙，原因是 CF 客户端拿到的 jar 与用户下载的 jar 可能行尾/缩进
    /// 不同（尤其 index 文件），剔掉空白后同一份内容仍能对上。
    ///
    /// 结果是 **32 位无符号**值（0 – 4294967295）。提交给 <c>/v1/fingerprints</c> 必须用无符号：
    /// 实测负值（有符号解释）一个都匹配不上，同一个文件换成无符号就能命中。
    ///
    /// 长度要先知道才能初始化哈希，所以扫两遍文件；两遍都是分块流式读，
    /// 几十 MB 的 jar 也不会整份进内存（FoldCraftLauncher 同样的两遍扫描）。
    /// </summary>
    public static class JarFingerprint
    {
        private const int BufferSize = 64 * 1024;

        /// <summary>
        /// 算一个 jar 的指纹。文件不存在或读不出来返回 0（指纹 0 不会是一个有效结果，
        /// 调用方据此跳过即可）。
        /// </summary>
        public static long OfFile(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            long length;
            try
            {
                using (var fs = File.OpenRead(path)) length = CountPayload(fs);
            }
            catch (Exception ex)
            {
                Log.Warn($"计算指纹时读取失败 {Path.GetFileName(path)}：{ex.Message}");
                return 0;
            }

            // 全空白（极小概率的损坏文件）没有可哈希内容
            if (length <= 0) return 0;

            try
            {
                using var stream = File.OpenRead(path);
                return Hash(stream, length);
            }
            catch (Exception ex)
            {
                Log.Warn($"计算指纹失败 {Path.GetFileName(path)}：{ex.Message}");
                return 0;
            }
        }

        private static long CountPayload(Stream s)
        {
            var buffer = new byte[BufferSize];
            long count = 0;
            int n;

            while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < n; i++)
                {
                    if (!IsWhitespace(buffer[i])) count++;
                }
            }

            return count;
        }

        private static bool IsWhitespace(byte b) => b == 9 || b == 10 || b == 13 || b == 32;

        /// <summary>
        /// 流式 MurmurHash2。边读边过滤空白，用一个 4 字节的小缓冲拼块，
        /// 所以过滤不会打断 4 字节分组。
        /// </summary>
        private static long Hash(Stream s, long length)
        {
            unchecked
            {
                const uint M = 0x5bd1e995;
                const int R = 24;

                var h = (uint)(1 ^ length);      // seed 固定为 1

                var buffer = new byte[BufferSize];
                var block = new byte[4];
                var have = 0;
                int n;

                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var b = buffer[i];
                        if (IsWhitespace(b)) continue;

                        block[have++] = b;
                        if (have < 4) continue;

                        var k = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
                        have = 0;

                        k *= M;
                        k ^= k >> R;
                        k *= M;

                        h *= M;
                        h ^= k;
                    }
                }

                switch (have)
                {
                    case 3:
                        h ^= (uint)block[2] << 16;
                        h ^= (uint)block[1] << 8;
                        h ^= block[0];
                        h *= M;
                        break;

                    case 2:
                        h ^= (uint)block[1] << 8;
                        h ^= block[0];
                        h *= M;
                        break;

                    case 1:
                        h ^= block[0];
                        h *= M;
                        break;
                }

                h ^= h >> 13;
                h *= M;
                h ^= h >> 15;

                // 保持无符号：uint → long 是零扩展
                return h;
            }
        }
    }
}
