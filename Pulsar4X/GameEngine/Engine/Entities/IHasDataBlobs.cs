using System.Collections.Generic;
using Pulsar4X.Datablobs;

namespace Pulsar4X.Engine;

public interface IHasDataBlobs
{
    void SetDataBlob<T>(T dataBlob) where T : BaseDataBlob;
    T GetDataBlob<T>() where T : BaseDataBlob;
    T GetRequiredDataBlob<T>() where T : BaseDataBlob;
    bool TryGetDataBlob<T>(out T? value) where T : BaseDataBlob;
    List<BaseDataBlob> GetAllDataBlobs();
}
