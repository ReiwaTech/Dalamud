using System.Collections.Generic;
using System.IO;

using Lumina;
using Lumina.Excel;
using Lumina.Excel.GeneratedSheets;
using Lumina.Text;
using Newtonsoft.Json;

namespace Dalamud.Data;

/// <summary>
/// Patch server names for CN client.
/// </summary>
internal class CNServerPatch
{
    private static Dictionary<uint, uint> dataCenterIdMap = new ()
    {
        { 1, 101 },
        { 6, 102 },
        { 7, 103 },
        { 8, 201 },
    };

    /// <summary>
    /// 为国服服务器临时修正isPublic & DataCenter数据.
    /// </summary>
    /// <param name="gameData">gameData</param>
    public static void ChangeWorldForCN(GameData gameData)
    {
        var dalamud = Service<Dalamud>.Get();
        var serverJson = File.ReadAllText(Path.Combine(dalamud.StartInfo.AssetDirectory!, "UIRes", "server.json"));
        var servers = JsonConvert.DeserializeObject<CNDataCenter[]>(serverJson);
        if (servers == null)
        {
            throw new InvalidDataException("Couldn't deserialize servers manifest.");
        }

        var dcExcel = gameData.Excel.GetSheet<WorldDCGroupType>();
        var worldExcel = gameData.Excel.GetSheet<World>();
        foreach (var dc in servers)
        {
            uint dcId;
            if (!dataCenterIdMap.TryGetValue(dc.DC, out dcId))
            {
                continue;
            }

            var dcToReplaced = dcExcel.GetRow(dcId);
            dcToReplaced.Name = new SeString(dc.Name);
            dcToReplaced.Region = 5;

            foreach (var world in dc.Worlds)
            {
                var worldToUpdated = worldExcel.GetRow(world.ID);
                worldToUpdated.IsPublic = true;
                worldToUpdated.DataCenter = new LazyRow<WorldDCGroupType>(gameData, dcId, Lumina.Data.Language.ChineseSimplified);
            }
        }
    }

    private class CNWorld
    {
        [JsonProperty("name_chs")]
        public string Name { get; private set; }

        [JsonProperty("id")]
        public uint ID { get; private set; }
    }

    private class CNDataCenter
    {
        [JsonProperty("name_chs")]
        public string Name { get; private set; }

        [JsonProperty("dc")]
        public uint DC { get; private set; }

        [JsonProperty("worlds")]
        public CNWorld[] Worlds { get; private set; }
    }
}
