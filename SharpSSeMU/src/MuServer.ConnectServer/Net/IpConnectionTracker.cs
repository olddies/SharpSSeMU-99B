namespace MuServer.ConnectServer.Net;

/// <summary>
/// Puerto de CIpManager: cuenta conexiones simultáneas por IP para aplicar MaxConnectionPerIP.
///
/// Nota de compatibilidad: el original devuelve "rechazado" para una IP nueva cuando
/// MaxConnectionPerIP=0 en el .ini (en vez de interpretarlo como "sin límite"). Es una rareza
/// del código original; se replica igual aquí para no cambiar el comportamiento observado en
/// producción. Si se prefiere "0 = sin límite", basta con cambiar la condición marcada abajo.
/// </summary>
public sealed class IpConnectionTracker
{
    private readonly Dictionary<string, int> _counts = new();
    private readonly object _sync = new();

    public int MaxConnectionPerIp { get; set; }

    public IpConnectionTracker(int maxConnectionPerIp)
    {
        MaxConnectionPerIp = maxConnectionPerIp;
    }

    public bool CheckIpAddress(string ipAddress)
    {
        lock (_sync)
        {
            if (!_counts.TryGetValue(ipAddress, out var count))
            {
                return MaxConnectionPerIp != 0; // ver nota de compatibilidad arriba
            }

            return count < MaxConnectionPerIp;
        }
    }

    public void InsertIpAddress(string ipAddress)
    {
        lock (_sync)
        {
            _counts[ipAddress] = _counts.GetValueOrDefault(ipAddress) + 1;
        }
    }

    public void RemoveIpAddress(string ipAddress)
    {
        lock (_sync)
        {
            if (!_counts.TryGetValue(ipAddress, out var count))
            {
                return;
            }

            if (count <= 1)
            {
                _counts.Remove(ipAddress);
            }
            else
            {
                _counts[ipAddress] = count - 1;
            }
        }
    }
}
