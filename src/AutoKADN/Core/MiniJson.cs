using System.Globalization;
using System.Text;

namespace AutoKADN.Core;

/// <summary>
/// Lector de JSON completo (objetos, listas, números, textos, true/false/null) sin librerías: sirve igual en el plugin
/// de 2022 (.NET Framework) y en el de 2027. Un objeto queda como Dictionary&lt;string, object?&gt;, una lista como
/// List&lt;object?&gt;, un número como double; el resto como string, bool o null.
/// </summary>
public static class MiniJson
{
    public static object? Parse(string json)
    {
        var reader = new Reader(json ?? string.Empty);
        reader.SkipSpace();
        object? value = reader.ReadValue();
        reader.SkipSpace();
        if (!reader.AtEnd) throw reader.Error("sobra texto después del final");
        return value;
    }

    private sealed class Reader
    {
        private readonly string _text;
        private int _position;

        public Reader(string text)
        {
            _text = text;
            if (_text.Length > 0 && _text[0] == '﻿') _position = 1;
        }

        public bool AtEnd => _position >= _text.Length;

        public FormatException Error(string message) => new FormatException($"JSON no válido (carácter {_position + 1}): {message}");

        public void SkipSpace()
        {
            while (_position < _text.Length && char.IsWhiteSpace(_text[_position])) _position++;
        }

        public object? ReadValue()
        {
            if (AtEnd) throw Error("se esperaba un valor");
            char c = _text[_position];
            switch (c)
            {
                case '{': return ReadObject();
                case '[': return ReadArray();
                case '"': return ReadString();
                case 't': ReadWord("true"); return true;
                case 'f': ReadWord("false"); return false;
                case 'n': ReadWord("null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                    throw Error($"no se esperaba «{c}»");
            }
        }

        private Dictionary<string, object?> ReadObject()
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            _position++; // {
            SkipSpace();
            if (!AtEnd && _text[_position] == '}') { _position++; return result; }
            while (true)
            {
                SkipSpace();
                if (AtEnd || _text[_position] != '"') throw Error("se esperaba el nombre de un campo entre comillas");
                string key = ReadString();
                SkipSpace();
                if (AtEnd || _text[_position] != ':') throw Error("se esperaba «:»");
                _position++;
                SkipSpace();
                result[key] = ReadValue();
                SkipSpace();
                if (AtEnd) throw Error("falta «}»");
                char c = _text[_position++];
                if (c == '}') return result;
                if (c != ',') throw Error("se esperaba «,» o «}»");
            }
        }

        private List<object?> ReadArray()
        {
            var result = new List<object?>();
            _position++; // [
            SkipSpace();
            if (!AtEnd && _text[_position] == ']') { _position++; return result; }
            while (true)
            {
                SkipSpace();
                result.Add(ReadValue());
                SkipSpace();
                if (AtEnd) throw Error("falta «]»");
                char c = _text[_position++];
                if (c == ']') return result;
                if (c != ',') throw Error("se esperaba «,» o «]»");
            }
        }

        private string ReadString()
        {
            _position++; // "
            var builder = new StringBuilder();
            while (true)
            {
                if (AtEnd) throw Error("falta cerrar las comillas");
                char c = _text[_position++];
                if (c == '"') return builder.ToString();
                if (c != '\\') { builder.Append(c); continue; }
                if (AtEnd) throw Error("escape incompleto");
                char next = _text[_position++];
                switch (next)
                {
                    case 'n': builder.Append('\n'); break;
                    case 'r': builder.Append('\r'); break;
                    case 't': builder.Append('\t'); break;
                    case 'b': builder.Append('\b'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'u':
                        if (_position + 4 > _text.Length || !int.TryParse(_text.Substring(_position, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            throw Error("escape \\u incompleto");
                        builder.Append((char)code);
                        _position += 4;
                        break;
                    default: builder.Append(next); break; // \" \\ \/
                }
            }
        }

        private double ReadNumber()
        {
            int start = _position;
            while (_position < _text.Length && "+-0123456789.eE".IndexOf(_text[_position]) >= 0) _position++;
            string token = _text.Substring(start, _position - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                throw Error($"número no válido «{token}»");
            return number;
        }

        private void ReadWord(string word)
        {
            if (string.CompareOrdinal(_text, _position, word, 0, word.Length) != 0) throw Error($"se esperaba «{word}»");
            _position += word.Length;
        }
    }
}
