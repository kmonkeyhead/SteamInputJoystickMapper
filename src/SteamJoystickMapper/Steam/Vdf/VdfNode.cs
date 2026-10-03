namespace SteamJoystickMapper.Steam.Vdf;

/// <summary>
/// Valve KeyValues(텍스트 VDF) 노드. 값 노드(Value) 또는 객체 노드(Children) 중 하나다.
/// 같은 키가 여러 번 나오는 경우("group" 등)와 원래 순서를 그대로 보존한다.
/// </summary>
public sealed class VdfNode
{
    public string Key { get; set; }
    public string? Value { get; set; }
    public List<VdfNode>? Children { get; private set; }

    public bool IsObject => Children != null;

    private VdfNode(string key) => Key = key;

    public static VdfNode CreateValue(string key, string value) => new(key) { Value = value };
    public static VdfNode CreateObject(string key) => new(key) { Children = new List<VdfNode>() };

    public VdfNode? this[string key] => Get(key);

    public VdfNode? Get(string key) =>
        Children?.FirstOrDefault(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<VdfNode> GetAll(string key) =>
        Children?.Where(c => string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase)) ?? Enumerable.Empty<VdfNode>();

    public string? GetValue(string key) => Get(key)?.Value;

    /// <summary>경로를 따라 객체를 찾는다. 대소문자 무시.</summary>
    public VdfNode? Find(params string[] path)
    {
        VdfNode? current = this;
        foreach (var key in path)
        {
            current = current?.Get(key);
            if (current == null) return null;
        }
        return current;
    }

    public VdfNode SetValue(string key, string value)
    {
        EnsureObject();
        var existing = Get(key);
        if (existing != null && !existing.IsObject)
        {
            existing.Value = value;
            return existing;
        }
        if (existing != null) Children!.Remove(existing);
        var node = CreateValue(key, value);
        Children!.Add(node);
        return node;
    }

    public VdfNode GetOrAddObject(string key)
    {
        EnsureObject();
        var existing = Get(key);
        if (existing is { IsObject: true }) return existing;
        if (existing != null) Children!.Remove(existing);
        var node = CreateObject(key);
        Children!.Add(node);
        return node;
    }

    public VdfNode Add(VdfNode child)
    {
        EnsureObject();
        Children!.Add(child);
        return child;
    }

    public bool Remove(string key)
    {
        var existing = Get(key);
        return existing != null && Children!.Remove(existing);
    }

    public bool Remove(VdfNode child) => Children?.Remove(child) ?? false;

    public void ClearChildren()
    {
        EnsureObject();
        Children!.Clear();
    }

    public VdfNode DeepClone()
    {
        if (!IsObject) return CreateValue(Key, Value ?? "");
        var clone = CreateObject(Key);
        foreach (var child in Children!) clone.Children!.Add(child.DeepClone());
        return clone;
    }

    private void EnsureObject()
    {
        if (Children == null) throw new InvalidOperationException($"'{Key}' is a value node, not an object.");
    }

    public override string ToString() => IsObject ? $"\"{Key}\" {{{Children!.Count}}}" : $"\"{Key}\" \"{Value}\"";
}
