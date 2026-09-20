namespace MuServer.GameServer.World;

/// <summary> Simplified port of PARTY_INFO (Party.h: <c>{ int Count; int Index[MAX_PARTY_USER]; }</c>, stored
/// in a flat array <c>m_PartyInfo[MAX_OBJECT]</c> in the original). Here directly a group with its own ID in
/// <see cref="PartyRegistry"/> -- more natural in C# than replicating the free-slot scan of the flat array; the
/// visible behaviour (maximum size, member order, slot 0 = leader) is the same. <b>Documented limitation of
/// this first pass</b>: members must be connected to the SAME GameServer process -- <see cref="MemberIndices"/>
/// is resolved against this process's local <see cref="PlayerRegistry"/>. The original does support parties
/// with members on different GameServers of the same realm (that is why PMSG_PARTY_LIST includes ServerCode per
/// member); here that field is always sent with the process's own ServerCode. </summary>
public sealed class PartyGroup
{
    public const int MaxMembers = 5;

    public required int Id { get; init; }

    /// <summary>PlayerObject indices in slot order -- slot 0 is the leader (see ChangeLeader).</summary>
    public List<int> MemberIndices { get; } = new();
}
