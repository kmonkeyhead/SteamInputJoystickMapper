using System.IO;
using System.Text;
using SteamJoystickMapper.Steam.Vdf;

namespace SteamJoystickMapper.Steam;

/// <summary>게임 속성 &gt; 컨트롤러 &gt; Steam Input 재정의.</summary>
public enum SteamInputMode { Default, Off, On }

/// <summary>
/// 게임별 Steam Input 사용 여부.
/// userdata/&lt;accountid&gt;/config/localconfig.vdf → UserLocalConfigStore/apps/&lt;appid&gt;/UseSteamControllerConfig
/// 값: "0" 사용 안 함, "1" 기본 설정, "2" 사용 (Steam UI 코드의 Off=0/Default=1/On=2, 사용자 PC에서 확인).
/// localconfig.vdf는 JSON 문자열 등 이스케이프가 많은 큰 파일이므로 전체를 다시 쓰지 않고 해당 값만 텍스트로 고친다.
/// </summary>
public static class SteamInputSetting
{
    public const string Key = "UseSteamControllerConfig";
    private const string RootKey = "UserLocalConfigStore";
    private const string AppsKey = "apps";

    public static string LocalConfigPath(string steamPath, uint accountId) =>
        Path.Combine(steamPath, "userdata", accountId.ToString(), "config", "localconfig.vdf");

    public static SteamInputMode Parse(string? value) => value switch
    {
        "0" => SteamInputMode.Off,
        "2" => SteamInputMode.On,
        _ => SteamInputMode.Default,
    };

    public static string DisplayName(SteamInputMode mode) => mode switch
    {
        SteamInputMode.On => "사용",
        SteamInputMode.Off => "사용 안 함",
        _ => "기본 설정 사용",
    };

    /// <summary>파일이 없거나 읽을 수 없으면 기본 설정으로 본다.</summary>
    public static SteamInputMode Read(string localConfigPath, string appId)
    {
        try
        {
            if (!File.Exists(localConfigPath)) return SteamInputMode.Default;
            return Read(VdfParser.ParseFile(localConfigPath), appId);
        }
        catch (Exception ex) when (ex is VdfParseException or IOException)
        {
            return SteamInputMode.Default;
        }
    }

    public static SteamInputMode Read(VdfNode root, string appId) =>
        Parse(root.Find(RootKey, AppsKey, appId)?.GetValue(Key));

    /// <summary>
    /// 지정한 게임들의 Steam Input을 '사용'으로 바꾼 텍스트를 만든다. 그 외 내용은 바이트 단위로 그대로 둔다.
    /// 결과를 파싱해 의도한 값만 바뀌었는지 확인하고, 아니면 예외를 던진다.
    /// </summary>
    public static string EnableFor(string text, IEnumerable<string> appIds)
    {
        var result = text;
        foreach (var appId in appIds) result = SetValue(result, appId, "2");

        var expected = VdfParser.Parse(text);
        var apps = expected.Get(RootKey)?.GetOrAddObject(AppsKey)
                   ?? throw new InvalidDataException($"localconfig.vdf에 {RootKey}가 없습니다.");
        foreach (var appId in appIds) apps.GetOrAddObject(appId).SetValue(Key, "2");
        if (!SameTree(expected, VdfParser.Parse(result)))
            throw new InvalidDataException("localconfig.vdf 수정 검증 실패: 의도하지 않은 변경이 생겼습니다.");
        return result;
    }

    private static string SetValue(string text, string appId, string value)
    {
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var root = Scan(text).FirstOrDefault(n => n.Key == RootKey && n.IsObject)
                   ?? throw new InvalidDataException($"localconfig.vdf에 {RootKey}가 없습니다.");
        var apps = root.Children.FirstOrDefault(n => n.Key.Equals(AppsKey, StringComparison.OrdinalIgnoreCase) && n.IsObject);
        if (apps == null)
            return InsertBefore(text, root.CloseBrace, 1, $"\"{AppsKey}\"{nl}\t{{{nl}{AppBlock(appId, value, 2, nl)}\t}}{nl}", nl);

        var app = apps.Children.FirstOrDefault(n => n.Key == appId && n.IsObject);
        if (app == null)
            return InsertBefore(text, apps.CloseBrace, 2, AppBlock(appId, value, 2, nl).TrimStart('\t'), nl);

        var existing = app.Children.FirstOrDefault(n => n.Key.Equals(Key, StringComparison.OrdinalIgnoreCase) && !n.IsObject);
        if (existing != null)
            return text[..existing.ValueStart] + $"\"{value}\"" + text[existing.ValueEnd..];
        return InsertBefore(text, app.CloseBrace, 3, $"\"{Key}\"\t\t\"{value}\"{nl}", nl);
    }

    private static string AppBlock(string appId, string value, int depth, string nl)
    {
        var i = new string('\t', depth);
        return $"{i}\"{appId}\"{nl}{i}{{{nl}{i}\t\"{Key}\"\t\t\"{value}\"{nl}{i}}}{nl}";
    }

    /// <summary>닫는 '}' 줄 앞에 넣는다. block은 첫 줄 들여쓰기를 뺀 내용.</summary>
    private static string InsertBefore(string text, int closeBrace, int depth, string block, string nl)
    {
        var lineStart = text.LastIndexOf('\n', closeBrace - 1) + 1;
        var onlyIndent = text[lineStart..closeBrace].All(c => c is ' ' or '\t');
        if (onlyIndent) return text[..lineStart] + new string('\t', depth) + block + text[lineStart..];
        return text[..closeBrace] + nl + new string('\t', depth) + block + text[closeBrace..];
    }

    private static bool SameTree(VdfNode a, VdfNode b) => VdfWriter.Write(a) == VdfWriter.Write(b);

    // ---------------- 위치를 기억하는 최소 스캐너 ----------------

    private sealed class Node
    {
        public string Key = "";
        public bool IsObject;
        public int CloseBrace;              // 객체
        public int ValueStart, ValueEnd;    // 값 (따옴표 포함 범위)
        public List<Node> Children = new();
    }

    private static List<Node> Scan(string text)
    {
        var pos = 0;
        var top = new List<Node>();
        ScanChildren(text, ref pos, top, isRoot: true);
        return top;
    }

    private static void ScanChildren(string text, ref int pos, List<Node> into, bool isRoot)
    {
        while (true)
        {
            var (kind, start, end, keyText) = NextToken(text, ref pos);
            if (kind == 0)
            {
                if (!isRoot) throw new InvalidDataException("localconfig.vdf: 닫는 '}'가 없습니다.");
                return;
            }
            if (kind == '}')
            {
                if (isRoot) throw new InvalidDataException("localconfig.vdf: 예상치 못한 '}'.");
                pos = start; // 호출자가 위치를 읽도록 되돌림
                return;
            }
            if (kind != 's') throw new InvalidDataException("localconfig.vdf: 키가 필요합니다.");

            var node = new Node { Key = keyText };
            var (vk, vs, ve, _) = NextToken(text, ref pos);
            if (vk == '{')
            {
                node.IsObject = true;
                ScanChildren(text, ref pos, node.Children, isRoot: false);
                var (ck, cs, _, _) = NextToken(text, ref pos);
                if (ck != '}') throw new InvalidDataException("localconfig.vdf: 닫는 '}'가 없습니다.");
                node.CloseBrace = cs;
            }
            else if (vk == 's')
            {
                node.ValueStart = vs;
                node.ValueEnd = ve;
            }
            else
            {
                throw new InvalidDataException($"localconfig.vdf: '{keyText}'의 값이 없습니다.");
            }
            into.Add(node);
        }
    }

    /// <summary>kind: 0 끝, '{', '}', 's' 문자열. start/end는 원문 범위(따옴표 포함).</summary>
    private static (char Kind, int Start, int End, string Text) NextToken(string text, ref int pos)
    {
        while (true)
        {
            while (pos < text.Length && (char.IsWhiteSpace(text[pos]) || text[pos] == '﻿')) pos++;
            if (pos >= text.Length) return ('\0', pos, pos, "");
            var c = text[pos];
            if (c == '/' && pos + 1 < text.Length && text[pos + 1] == '/')
            {
                while (pos < text.Length && text[pos] != '\n') pos++;
                continue;
            }
            if (c == '[')
            {
                while (pos < text.Length && text[pos] != ']') pos++;
                pos++;
                continue;
            }
            if (c is '{' or '}') { pos++; return (c, pos - 1, pos, c.ToString()); }

            var start = pos;
            var sb = new StringBuilder();
            if (c == '"')
            {
                pos++;
                while (pos < text.Length && text[pos] != '"')
                {
                    if (text[pos] == '\\' && pos + 1 < text.Length) { sb.Append(text[pos]); pos++; }
                    sb.Append(text[pos++]);
                }
                if (pos >= text.Length) throw new InvalidDataException("localconfig.vdf: 닫는 따옴표가 없습니다.");
                pos++;
            }
            else
            {
                while (pos < text.Length && !char.IsWhiteSpace(text[pos]) && text[pos] is not ('{' or '}' or '"')) sb.Append(text[pos++]);
            }
            return ('s', start, pos, sb.ToString());
        }
    }
}
