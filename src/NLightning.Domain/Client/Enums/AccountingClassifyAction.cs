namespace NLightning.Domain.Client.Enums;

/// <summary>
/// What <c>accounting classify</c> does (<c>ClientCommand.AccountingAdmin</c> with
/// <see cref="AccountingAdminAction.Classify"/>, NL-602 A3-T3). The values go on the wire: never renumber them.
/// </summary>
public enum AccountingClassifyAction
{
    /// <summary><c>classify rule add</c>: store a rule.</summary>
    RuleAdd = 1,

    /// <summary><c>classify rule list</c>: every rule in match order.</summary>
    RuleList = 2,

    /// <summary><c>classify rule remove &lt;id&gt;</c>.</summary>
    RuleRemove = 3,

    /// <summary><c>classify rule test &lt;event key&gt;</c>: classify one event with the stored rules (and a candidate
    /// rule, when given).</summary>
    RuleTest = 4,

    /// <summary><c>classify rule enable &lt;id&gt;</c>.</summary>
    RuleEnable = 5,

    /// <summary><c>classify rule disable &lt;id&gt;</c>.</summary>
    RuleDisable = 6,

    /// <summary><c>classify set &lt;event key&gt; &lt;account&gt;</c>: store an override.</summary>
    Set = 7,

    /// <summary><c>classify unset &lt;event key&gt;</c>: remove an override.</summary>
    Unset = 8,

    /// <summary><c>classify list</c>: the overrides.</summary>
    ListOverrides = 9,

    /// <summary><c>classify list --unclassified</c>: the entries that go to an unclassified account.</summary>
    ListUnclassified = 10
}