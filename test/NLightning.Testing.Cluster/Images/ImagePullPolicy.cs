namespace NLightning.Testing.Cluster.Images;

/// <summary>
/// The Kubernetes <c>imagePullPolicy</c> of a container.
/// </summary>
public enum ImagePullPolicy
{
    /// <summary>Use the image the node already has; pull it otherwise (pinned public images).</summary>
    IfNotPresent,

    /// <summary>Never pull: the image was built locally (OrbStack shares the Docker image store with the cluster).</summary>
    Never,

    /// <summary>Always pull (never used for pinned images).</summary>
    Always
}