using System.Text;

namespace SteamJoystickMapper.Steam.Vdf;

/// <summary>Steam 클라이언트와 같은 스타일(탭 들여쓰기, 키/값 사이 탭 2개)로 VDF를 출력한다.</summary>
public static class VdfWriter
{
    public static string Write(VdfNode root)
    {
        var sb = new StringBuilder();
        if (root.Key.Length == 0 && root.IsObject)
        {
            foreach (var child in root.Children!) WriteNode(sb, child, 0);
        }
        else
        {
            WriteNode(sb, root, 0);
        }
        return sb.ToString();
    }

    private static void WriteNode(StringBuilder sb, VdfNode node, int depth)
    {
        var indent = new string('\t', depth);
        if (node.IsObject)
        {
            sb.Append(indent).Append(Quote(node.Key)).Append('\n');
            sb.Append(indent).Append("{\n");
            foreach (var child in node.Children!) WriteNode(sb, child, depth + 1);
            sb.Append(indent).Append("}\n");
        }
        else
        {
            sb.Append(indent).Append(Quote(node.Key)).Append("\t\t").Append(Quote(node.Value ?? "")).Append('\n');
        }
    }

    private static string Quote(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
