using System.IO;
using System.Text.Json;

namespace ChipL.Core;

public enum ChipModule
{
    Cpu,
    Gpu,
    Soc
}

/// <summary>数据库版本信息（与 App 服务端 /db 接口约定一致）。</summary>
public sealed class DbInfo
{
    public int Release { get; set; }
    public int Version { get; set; }
    public string Url { get; set; } = "";
    public int Size { get; set; }
}

/// <summary>
/// 还原自 Android 版的数据库读取逻辑。
/// 线上数据 = AES-256-CFB8(包名 MD5 十六进制作 key / key 倒序取字符作 IV) 包裹的 ZIP，内含单个 JSON。
/// </summary>
public static class DbCrypto
{
    public static string PackageFor(ChipModule m) => m switch
    {
        ChipModule.Cpu => "com.ijsoft.cpul",
        ChipModule.Gpu => "com.ijsoft.gpul",
        ChipModule.Soc => "com.ijsoft.socl",
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    };

    public static string JsonFileNameFor(ChipModule m) => m switch
    {
        ChipModule.Cpu => "cpus_db",
        ChipModule.Gpu => "gpus_db",
        ChipModule.Soc => "socs_db",
        _ => throw new ArgumentOutOfRangeException(nameof(m))
    };

    /// <summary>包名 MD5 小写十六进制。</summary>
    public static string KeyHex(ChipModule m)
    {
        byte[] hash = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(PackageFor(m)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>与 Android 版 a6.h.g 一致：hex 字符串从末位往前两两取字符组成 16 字节 IV。</summary>
    public static byte[] IvFromHex(string hex)
    {
        var iv = new byte[16];
        for (int i = 0; i < 16; i++)
            iv[i] = (byte)hex[hex.Length - 1 - 2 * i];
        return iv;
    }

    public static byte[] Decrypt(ChipModule m, byte[] data)
    {
        string hex = KeyHex(m);
        byte[] key = System.Text.Encoding.ASCII.GetBytes(hex);
        byte[] iv = IvFromHex(hex);
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = System.Security.Cryptography.CipherMode.CFB;
        aes.FeedbackSize = 8;
        aes.Padding = System.Security.Cryptography.PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(data, 0, data.Length);
    }

    /// <summary>解密并取出 ZIP 里的 JSON 文本；结构不符时抛出异常。</summary>
    public static string Unpack(ChipModule m, byte[] payload, out string asZipName)
    {
        asZipName = "";
        byte[] plain = Decrypt(m, payload);
        if (!(plain.Length > 4 && plain[0] == 0x50 && plain[1] == 0x4B))
            throw new InvalidDataException("解密结果不是 ZIP 包，密钥或数据不匹配");

        using var ms = new MemoryStream(plain, writable: false);
        using var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Read);
        var entry = zip.Entries.FirstOrDefault();
        if (entry == null) throw new InvalidDataException("ZIP 内没有数据文件");
        asZipName = entry.FullName;
        using var reader = new StreamReader(entry.Open(), System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
