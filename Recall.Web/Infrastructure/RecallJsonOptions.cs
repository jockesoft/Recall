using System.Text.Json;

namespace Recall.Web.Infrastructure;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance the app uses for its
/// own (de)serialization — cache snapshots, DB-stored payloads, and outbound
/// login/token bodies. All of it is just <see cref="JsonSerializerDefaults.Web"/>;
/// this exists so that ever needing a shared setting (a converter, explicit
/// case-insensitivity, etc.) means changing one place instead of the several
/// identical private fields this used to be copy-pasted into.
/// </summary>
public static class RecallJsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
