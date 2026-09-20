namespace MuServer.GameServer.World;

/// <summary>
/// Puerto simplificado de PARTY_INFO (Party.h: <c>{ int Count; int Index[MAX_PARTY_USER]; }</c>,
/// guardado en un array plano <c>m_PartyInfo[MAX_OBJECT]</c> en el original). Acá directamente un
/// grupo con ID propio en <see cref="PartyRegistry"/> -- más natural en C# que replicar el escaneo
/// de slot libre del array plano; el comportamiento visible (tamaño máximo, orden de miembros,
/// slot 0 = líder) es el mismo.
///
/// <b>Limitación documentada de esta primera pasada</b>: los miembros deben estar conectados al
/// MISMO proceso de GameServer -- <see cref="MemberIndices"/> se resuelve contra el
/// <see cref="PlayerRegistry"/> local de este proceso. El original sí soporta grupos con miembros
/// en distintos GameServers de un mismo realm (por eso PMSG_PARTY_LIST incluye ServerCode por
/// miembro); acá ese campo se manda siempre con el ServerCode propio del proceso.
/// </summary>
public sealed class PartyGroup
{
    public const int MaxMembers = 5;

    public required int Id { get; init; }

    /// <summary>Índices de PlayerObject en orden de slot -- slot 0 es el líder (ver ChangeLeader).</summary>
    public List<int> MemberIndices { get; } = new();
}
