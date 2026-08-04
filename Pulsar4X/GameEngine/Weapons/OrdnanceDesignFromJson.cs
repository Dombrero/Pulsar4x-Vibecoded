using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using Pulsar4X.Components;
using Pulsar4X.Engine;
using Pulsar4X.Factions;

namespace Pulsar4X.Weapons;

public static class OrdnanceDesignFromJson
{
    public static OrdnanceDesign Create(Entity faction, string filePath)
    {
        string fileContents = File.ReadAllText(filePath);
        var rootJson = JObject.Parse(fileContents);

        var designName = rootJson["name"]?.ToString() ?? throw new InvalidOperationException("Ordnance design json missing name.");
        var id = rootJson["id"] == null ? null : rootJson["id"]?.ToString();
        var fuelAmount = (double?)rootJson["fuelAmount"] ?? 0;

        var factionInfoDB = faction.GetDataBlob<FactionInfoDB>();
        var ordnanceComponents = new List<(ComponentDesign, int)>();


        var components = (JArray?)rootJson["components"];
        if (components != null)
        {
            foreach (var component in components)
            {
                var componentId = component["id"]?.ToString() ?? throw new InvalidOperationException("Ordnance component missing id.");
                var amount = (int?)component["amount"] ?? 0;

                ordnanceComponents.Add((
                  factionInfoDB.InternalComponentDesigns[componentId],
                  amount
                ));
            }
        }

        return new OrdnanceDesign(factionInfoDB, designName, fuelAmount, ordnanceComponents, id, true);
    }
}