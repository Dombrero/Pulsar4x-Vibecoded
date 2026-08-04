using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Pulsar4X.Engine;

namespace Pulsar4X.Names
{
    public static class NameLookup
    {

        internal static bool TryGetFirstEntityWithName(EntityManager manager, string name, [NotNullWhen(true)] out Entity? entity)
        {
            List<Entity> list = manager.GetAllEntitiesWithDataBlob<NameDB>();
            return TryGetFirstEntityWithName(list, name, out entity);
        }
        internal static bool TryGetFirstEntityWithName(List<Entity> entitiesWithNameDB, string name, [NotNullWhen(true)] out Entity? entity)
        {
            foreach (var item in entitiesWithNameDB)
            {
                NameDB namedb = item.GetDataBlob<NameDB>();
                if (namedb.DefaultName == name)
                {
                    entity = item;
                    return true;
                }
            }
            entity = null;
            return false;
        }

        internal static Entity GetFirstEntityWithName(EntityManager manager, string name)
        {
            List<Entity> list = manager.GetAllEntitiesWithDataBlob<NameDB>();
            return GetFirstEntityWithName(list, name);
        }

        internal static Entity GetFirstEntityWithName(List<Entity> entitiesWithNameDB, string name)
        {
            foreach (var item in entitiesWithNameDB)
            {
                NameDB namedb = item.GetDataBlob<NameDB>();
                if (namedb.DefaultName == name)
                {
                    return item;
                }
            }
            throw new Exception(name + " not found");
        }
    }
}