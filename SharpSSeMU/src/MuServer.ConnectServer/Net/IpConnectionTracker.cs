namespace MuServer.ConnectServer.Net;

/// | <summary> Port of CIpManager: counts simultaneous connections per IP to enforce MaxConnectionPerIP.
/// Compatibility note: the original returns "rejected" for a new IP when MaxConnectionPerIP=0 in the .ini
/// (instead of interpreting it as "no limit"). It is an oddity of the original code; it is replicated here
/// anyway so as not to change the behaviour observed in production. If "0 = no limit" is preferred, it is
/// enough to change the condition marked below. </summary>
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
