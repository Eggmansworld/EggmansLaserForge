using System;
using System.Collections.Generic;
using System.Linq;

namespace Ldp.App;

/// <summary>
/// Scenes cut or copied for level assignment. The SCENES list does the cutting
/// and copying; the Game Setup levels panel does the pasting, so the two
/// surfaces need somewhere shared to hand scenes over.
///
/// Cut and copy differ at paste time: a cut scene MOVES into the target level
/// (a scene plays in exactly one level), while a copied scene is DUPLICATED —
/// a second scene over the same frames — so a passage can be replayed in a
/// later level without stealing it from the first.
/// </summary>
public static class SceneClipboard
{
    public static IReadOnlyList<Guid> Ids { get; private set; } = [];

    /// <summary>True when the scenes were cut (move) rather than copied (duplicate).</summary>
    public static bool IsCut { get; private set; }

    public static bool HasContent => Ids.Count > 0;

    public static void Set(IEnumerable<Guid> ids, bool cut)
    {
        Ids = ids.ToList();
        IsCut = cut;
    }

    public static void Clear()
    {
        Ids = [];
        IsCut = false;
    }

    /// <summary>Button/menu label describing what a paste would do right now.</summary>
    public static string PasteLabel =>
        !HasContent ? "📋 Paste"
        : IsCut ? $"📋 Paste {Ids.Count} (move)"
        : $"📋 Paste {Ids.Count} (copy)";
}
