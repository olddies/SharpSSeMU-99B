namespace MuServer.AdminPanel.Config;

/// <summary>Raíz del `Data/` real del GameServer que este panel lee y escribe. Un solo punto de
/// verdad para que cada repositorio arme su propia ruta (`Move/Move.txt`, `Item/Item.txt`, etc.)
/// sin repetir el cálculo de "dónde está Data".</summary>
public sealed class GameDataPaths(string root)
{
    public string Root { get; } = root;

    public string Combine(params string[] parts) => Path.Combine([Root, .. parts]);

    public string StatusJson => Combine("status.json");
}
