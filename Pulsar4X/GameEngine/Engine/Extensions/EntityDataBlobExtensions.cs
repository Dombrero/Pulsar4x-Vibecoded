using System;
using Pulsar4X.Datablobs;
using Pulsar4X.Engine;

namespace Pulsar4X.Extensions;

public static class EntityDataBlobExtensions
{
    public static T GetRequiredDataBlob<T>(this IHasDataBlobs host) where T : BaseDataBlob
    {
        ArgumentNullException.ThrowIfNull(host);
        return host switch
        {
            Entity entity => entity.GetDataBlob<T>(),
            ProtoEntity proto => proto.GetDataBlob<T>(),
            _ => host.GetRequiredDataBlob<T>()
        };
    }
}
