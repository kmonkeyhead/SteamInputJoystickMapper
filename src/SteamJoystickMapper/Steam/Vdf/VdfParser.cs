using System.IO;
using System.Text;

namespace SteamJoystickMapper.Steam.Vdf;

public sealed class VdfParseException(string message, int line) : Exception($"VDF 구문 오류 (줄 {line}): {message}")
{
    public int Line { get; } = line;
}

/// <summary>
/// 텍스트 VDF 파서. 루트는 가상의 객체 노드("")이며 최상위 키들을 자식으로 갖는다.
/// 지원: 따옴표/비따옴표 토큰, 이스케이프(\\ \" \n \t), // 주석, [$조건] 태그(무시).
/// 따옴표 안의 실제 줄바꿈(config.vdf의 SDL_GamepadBind 등)은 그대로 보존한다.
/// </summary>
public static class VdfParser
{
    public static VdfNode ParseFile(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public static VdfNode Parse(string text)
    {
        var reader = new Tokenizer(text);
        var root = VdfNode.CreateObject("");
        ParseChildren(reader, root, isRoot: true);
        return root;
    }

    private static void ParseChildren(Tokenizer reader, VdfNode parent, bool isRoot)
    {
        while (true)
        {
            var token = reader.Next();
            if (token == null)
            {
                if (!isRoot) throw new VdfParseException("닫는 '}'가 없습니다.", reader.Line);
                return;
            }
            if (token.Kind == TokenKind.CloseBrace)
            {
                if (isRoot) throw new VdfParseException("예상치 못한 '}'.", reader.Line);
                return;
            }
            if (token.Kind != TokenKind.String)
                throw new VdfParseException("키가 필요합니다.", reader.Line);

            var key = token.Text;
            var next = reader.Next() ?? throw new VdfParseException($"'{key}'의 값이 없습니다.", reader.Line);
            if (next.Kind == TokenKind.OpenBrace)
            {
                var obj = VdfNode.CreateObject(key);
                ParseChildren(reader, obj, isRoot: false);
                parent.Add(obj);
            }
            else if (next.Kind == TokenKind.String)
            {
                parent.Add(VdfNode.CreateValue(key, next.Text));
            }
            else
            {
                throw new VdfParseException($"'{key}' 다음에 예상치 못한 '}}'.", reader.Line);
            }
        }
    }

    private enum TokenKind { String, OpenBrace, CloseBrace }

    private sealed record Token(TokenKind Kind, string Text);

    private sealed class Tokenizer(string text)
    {
        private int _pos;
        public int Line { get; private set; } = 1;

        public Token? Next()
        {
            while (true)
            {
                SkipWhitespace();
                if (_pos >= text.Length) return null;
                var c = text[_pos];

                if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
                {
                    while (_pos < text.Length && text[_pos] != '\n') _pos++;
                    continue;
                }
                if (c == '[')
                {
                    // 플랫폼 조건 태그 ([$WIN32] 등)는 무시
                    while (_pos < text.Length && text[_pos] != ']') _pos++;
                    _pos++;
                    continue;
                }
                if (c == '{') { _pos++; return new Token(TokenKind.OpenBrace, "{"); }
                if (c == '}') { _pos++; return new Token(TokenKind.CloseBrace, "}"); }
                if (c == '"') return new Token(TokenKind.String, ReadQuoted());
                return new Token(TokenKind.String, ReadUnquoted());
            }
        }

        private void SkipWhitespace()
        {
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\n') Line++;
                else if (!char.IsWhiteSpace(c) && c != '﻿') break;
                _pos++;
            }
        }

        private string ReadQuoted()
        {
            var startLine = Line;
            _pos++; // opening quote
            var sb = new StringBuilder();
            while (_pos < text.Length)
            {
                var c = text[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\n') Line++;
                if (c == '\\' && _pos < text.Length)
                {
                    var e = text[_pos];
                    switch (e)
                    {
                        case '\\': sb.Append('\\'); _pos++; continue;
                        case '"': sb.Append('"'); _pos++; continue;
                        case 'n': sb.Append('\n'); _pos++; continue;
                        case 't': sb.Append('\t'); _pos++; continue;
                    }
                }
                sb.Append(c);
            }
            throw new VdfParseException("닫는 따옴표가 없습니다.", startLine);
        }

        private string ReadUnquoted()
        {
            var start = _pos;
            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (char.IsWhiteSpace(c) || c == '{' || c == '}' || c == '"') break;
                _pos++;
            }
            return text[start.._pos];
        }
    }
}
