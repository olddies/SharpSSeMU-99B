using System.Text;

namespace MuServer.Shared.Scripting;

public enum TokenResult
{
    Number = 0,
    String = 1,
    End = 2,
    Error = 3,
}

/// <summary> 1:1 port of the "CMemScript" tokenizer used by all the processes of the original server
/// (ConnectServer/JoinServer/DataServer/GameServer) to read the plain-text configuration files of Data/
/// (ServerList.dat, BlackList.txt, Item.txt, MonsterList.txt, etc). Rules replicated from the original: - "//"
/// starts a comment until the end of the line. - Numbers can include digits, '.', '-' and '*' (where "*" is
/// interpreted as -1, used in several MU data files for "no limit"/"any"). - Strings go between double quotes.
/// - Identifiers (unquoted words) start with a letter and continue with alphanumerics, '.' or '_'. - End of
/// file returns TOKEN_END. Note: the original had a 1-second watchdog per parse (m_tick) that threw an
/// exception if a file took too long to tokenise; it is omitted here because it was a protection against hangs
/// of the original thread and is not part of the data format itself. </summary>
public class MemScript
{
    private byte[] _buff = Array.Empty<byte>();
    private int _count;
    private string _path = string.Empty;
    private float _number;
    private string _string = string.Empty;
    private string _lastError = string.Empty;

    public bool SetBuffer(string path)
    {
        _path = path;

        if (!File.Exists(path))
        {
            SetLastError(0);
            return false;
        }

        try
        {
            _buff = File.ReadAllBytes(path);
        }
        catch
        {
            SetLastError(2);
            return false;
        }

        if (_buff.Length >= 3 && _buff[0] == 0xEF && _buff[1] == 0xBB && _buff[2] == 0xBF)
        {
            SetLastError(5);
            return false;
        }

        _count = 0;
        return true;
    }

    private int GetChar()
    {
        if (_count >= _buff.Length)
        {
            return -1;
        }

        return _buff[_count++];
    }

    private void UnGetChar()
    {
        if (_count == 0)
        {
            return;
        }

        _count--;
    }

    // Returns -1 at EOF, or the char (possibly '\n' if it was a comment ending in a line break).
    private int CheckComment(int ch)
    {
        if (ch != '/')
        {
            return ch;
        }

        int next = GetChar();

        if (next != '/')
        {
            // Not a comment: the character following "/" is not "/", we push it back.
            if (next != -1)
            {
                UnGetChar();
            }
            return ch;
        }

        while (true)
        {
            int c = GetChar();

            if (c == -1)
            {
                return -1;
            }

            if (c == '\n')
            {
                return '\n';
            }
        }
    }

    public TokenResult GetToken()
    {
        _number = 0;
        _string = string.Empty;

        int ch;

        while (true)
        {
            ch = GetChar();

            if (ch == -1)
            {
                return TokenResult.End;
            }

            if (char.IsWhiteSpace((char)ch))
            {
                continue;
            }

            int afterComment = CheckComment(ch);

            if (afterComment == -1)
            {
                return TokenResult.End;
            }

            if (afterComment != '\n')
            {
                ch = afterComment;
                break;
            }
        }

        if (ch == '-' || ch == '.' || ch == '*' || (ch >= '0' && ch <= '9'))
        {
            return GetTokenNumber(ch);
        }

        if (ch == '"')
        {
            return GetTokenString();
        }

        return GetTokenCommon(ch);
    }

    private TokenResult GetTokenNumber(int first)
    {
        var sb = new StringBuilder();
        UnGetChar();

        int ch;
        while ((ch = GetChar()) != -1 && (ch == '-' || ch == '.' || ch == '*' || char.IsDigit((char)ch)))
        {
            sb.Append((char)ch);
        }

        if (ch != -1)
        {
            UnGetChar();
        }

        _string = sb.ToString();

        _number = _string == "*" ? -1f : float.Parse(_string == string.Empty ? "0" : _string, System.Globalization.CultureInfo.InvariantCulture);

        return TokenResult.Number;
    }

    private TokenResult GetTokenString()
    {
        var sb = new StringBuilder();

        int ch;
        while ((ch = GetChar()) != -1 && ch != '"')
        {
            sb.Append((char)ch);
        }

        // If ch != '"' (EOF without closing quote) the original does UnGetChar; replicated even though it is EOF-safe here.
        _string = sb.ToString();

        return TokenResult.String;
    }

    private TokenResult GetTokenCommon(int first)
    {
        if (!char.IsLetter((char)first))
        {
            return TokenResult.Error;
        }

        var sb = new StringBuilder();
        sb.Append((char)first);

        int ch;
        while ((ch = GetChar()) != -1 && (ch == '.' || ch == '_' || char.IsLetterOrDigit((char)ch)))
        {
            sb.Append((char)ch);
        }

        if (ch != -1)
        {
            UnGetChar();
        }

        _string = sb.ToString();

        return TokenResult.String;
    }

    private void SetLastError(int error)
    {
        _lastError = error switch
        {
            0 => $"[{_path}] Could not open file",
            1 => $"[{_path}] Could not alloc file buffer",
            2 => $"[{_path}] Could not read file",
            3 => $"[{_path}] Could not get file buffer",
            4 => $"[{_path}] The file were not configured correctly",
            5 => $"[{_path}] The file is codified in UTF-8 BOM and cannot be read. Change it to UTF-8 and try again",
            _ => $"[{_path}] Unknow error code: {error}",
        };
    }

    public string GetLastError() => _lastError;

    public int GetNumber() => (int)_number;
    public int GetAsNumber() { GetToken(); return (int)_number; }
    public float GetFloatNumber() => _number;
    public float GetAsFloatNumber() { GetToken(); return _number; }
    public string GetString() => _string;
    public string GetAsString() { GetToken(); return _string; }
}
