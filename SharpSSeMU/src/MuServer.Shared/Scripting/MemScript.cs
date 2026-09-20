using System.Text;

namespace MuServer.Shared.Scripting;

public enum TokenResult
{
    Number = 0,
    String = 1,
    End = 2,
    Error = 3,
}

/// <summary>
/// Puerto 1:1 del tokenizer "CMemScript" usado por todos los procesos del servidor original
/// (ConnectServer/JoinServer/DataServer/GameServer) para leer los archivos de configuración en
/// texto plano de Data/ (ServerList.dat, BlackList.txt, Item.txt, MonsterList.txt, etc).
///
/// Reglas replicadas del original:
///  - "//" inicia un comentario hasta fin de línea.
///  - Los números pueden incluir dígitos, '.', '-' y '*' (donde "*" se interpreta como -1,
///    usado en varios archivos de datos de MU para "sin límite"/"cualquiera").
///  - Los strings van entre comillas dobles.
///  - Los identificadores (palabras sin comillas) empiezan con letra y siguen con alfanumérico, '.' o '_'.
///  - Fin de archivo devuelve TOKEN_END.
///
/// Nota: el original tenía un watchdog de 1 segundo por parseo (m_tick) que lanzaba una excepción
/// si un archivo tardaba demasiado en tokenizarse; se omite aquí porque era una protección contra
/// cuelgues del hilo original y no forma parte del formato de datos en sí.
/// </summary>
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

    // Devuelve -1 en EOF, o el char (posiblemente '\n' si era un comentario terminado en salto de línea).
    private int CheckComment(int ch)
    {
        if (ch != '/')
        {
            return ch;
        }

        int next = GetChar();

        if (next != '/')
        {
            // No es comentario: el carácter que sigue a "/" no es "/", lo regresamos.
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

        // Si ch != '"' (EOF sin cerrar comillas) el original hace UnGetChar; replicado aunque sea EOF-safe aquí.
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
