namespace NLightning.Testing.Cluster.Images;

/// <summary>
/// One container image of the version table (plan R12): a repository, a tag, an optional digest and the pull policy
/// that goes with where the image comes from.
/// </summary>
/// <param name="Repository">The repository, e.g. <c>elementsproject/lightningd</c>.</param>
/// <param name="Tag">The tag, e.g. <c>v26.06.8</c>.</param>
/// <param name="Digest">The <c>sha256:</c> digest the image is pinned to, or null for a tag-only (local) image.</param>
/// <param name="PullPolicy">How the cluster gets the image.</param>
public sealed record ImageRef(string Repository, string Tag, string? Digest = null,
                              ImagePullPolicy PullPolicy = ImagePullPolicy.IfNotPresent)
{
    /// <summary>
    /// The image reference for a pod spec: <c>repository:tag@digest</c> when pinned (the runtime resolves the digest
    /// and the tag stays readable), <c>repository:tag</c> otherwise.
    /// </summary>
    public string Reference => Digest is null ? $"{Repository}:{Tag}" : $"{Repository}:{Tag}@{Digest}";

    /// <summary>The <c>imagePullPolicy</c> value of a pod spec.</summary>
    public string PullPolicyValue => PullPolicy.ToString();

    public override string ToString() => Reference;
}