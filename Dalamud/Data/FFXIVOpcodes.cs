using System;
using System.Collections.Generic;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Dalamud.Networking.Http;
using Newtonsoft.Json;
using Serilog;

namespace Dalamud.Data;

/// <summary>
/// Provide Opcodes from Github (karashiiro/FFXIVOpcodes).
/// </summary>
internal class FFXIVOpcodes
{
    private const string Url = "https://raw.githubusercontent.com/karashiiro/FFXIVOpcodes/master/opcodes.min.json";

    private string region;

    /// <summary>
    /// Initializes a new instance of the <see cref="FFXIVOpcodes"/> class.
    /// </summary>
    /// <param name="region">Region (Global / CN / KR).</param>
    internal FFXIVOpcodes(string region)
    {
        this.region = region;
        this.Ready = false;
        this.ClientOpCodes = new();
        this.ServerOpCodes = new();
    }

    /// <summary>
    /// Gets ServerZoneIpcType Opcodes.
    /// </summary>
    public Dictionary<string, ushort> ClientOpCodes { get; private set; }

    /// <summary>
    /// Gets ServerZoneIpcType Opcodes.
    /// </summary>
    public Dictionary<string, ushort> ServerOpCodes { get; private set; }

    /// <summary>
    /// Gets a value indicating whether remote data loaded or not.
    /// </summary>
    public bool Ready { get; private set; }

    /// <summary>
    /// Read FFXIVOpcodes.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    internal async Task Init()
    {
        var client = Service<HappyHttpClient>.Get().SharedHttpClient;
        try
        {
            var data = await client.GetFromJsonAsync<OpcodeData[]>(Url);
            foreach (var row in data)
            {
                if (row.Region != this.region)
                {
                    continue;
                }

                if (row.Lists.TryGetValue("ServerZoneIpcType", out var serverList))
                {
                    this.ServerOpCodes.Clear();
                    foreach (var item in serverList)
                    {
                        this.ServerOpCodes.Add(item.Name, item.Opcode);
                    }

                    Log.Verbose("Loaded {0} ServerOpCodes from FFXIVOpcodes.", this.ServerOpCodes.Count);
                }

                if (row.Lists.TryGetValue("ClientZoneIpcType", out var clientList))
                {
                    this.ClientOpCodes.Clear();
                    foreach (var item in clientList)
                    {
                        this.ClientOpCodes.Add(item.Name, item.Opcode);
                    }

                    Log.Verbose("Loaded {0} ClientOpCodes from FFXIVOpcodes.", this.ClientOpCodes.Count);
                }

                return;
            }

            Log.Warning("Failed loading region {0} from FFXIVOpcodes.", this.region);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not load FFXIVOpcodes.");
        }

        this.Ready = true;
    }

    private class OpcodeItem
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("opcode")]
        public ushort Opcode { get; set; }
    }

    private class OpcodeData
    {
        [JsonProperty("version")]
        public string Version { get; set; }

        [JsonProperty("region")]
        public string Region { get; set; }

        [JsonProperty("lists")]
        public Dictionary<string, OpcodeItem[]> Lists { get; set; }
    }
}
