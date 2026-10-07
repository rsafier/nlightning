using System.Reflection;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Microsoft.AspNetCore.Http;

namespace NLightning.LndGrpc;

/// <summary>
/// Answers calls to services and methods this server does not implement as grpc-go (LND's server) does (NL-1243):
/// <c>UNIMPLEMENTED</c> with <c>unknown service &lt;package.Service&gt;</c> for a service that is not registered and
/// <c>unknown method &lt;Method&gt; for service &lt;package.Service&gt;</c> for a method the service does not have or
/// that this node does not implement. LND clients match that text to fall back (ln-service: <c>/unknown/</c>,
/// <c>unknown service walletrpc.WalletKit</c>, ...); grpc-dotnet's own answers ("Service is unimplemented.", or an
/// empty message from a generated base method) defeat those fallbacks.
/// </summary>
/// <remarks>
/// The check runs before the macaroon interceptor, as grpc-go refuses an unknown method before any interceptor. A
/// method is implemented when the registered service type overrides the generated base method.
/// </remarks>
internal sealed class LndUnknownMethods
{
    private const string GrpcContentType = "application/grpc";

    /// <summary>Per registered service (full proto name): the methods the node implements.</summary>
    private readonly Dictionary<string, HashSet<string>> _implemented = new(StringComparer.Ordinal);

    /// <param name="serviceTypes">The registered service implementations (subclasses of the generated
    /// <c>XBase</c> classes).</param>
    public LndUnknownMethods(IEnumerable<Type> serviceTypes)
    {
        foreach (var type in serviceTypes)
        {
            var descriptor = FindDescriptor(type)
                          ?? throw new ArgumentException($"{type} is not a generated gRPC service implementation.",
                                                         nameof(serviceTypes));
            var implemented = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in descriptor.Methods)
            {
                var overridden = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                     .Any(m => m.Name == method.Name && m.DeclaringType == type);
                if (overridden)
                    implemented.Add(method.Name);
            }

            _implemented[descriptor.FullName] = implemented;
        }
    }

    /// <summary>
    /// The LND error for <paramref name="path"/> (<c>/package.Service/Method</c>), or null when the node implements
    /// the method (or the path is not a gRPC method path).
    /// </summary>
    public string? GetError(string path)
    {
        if (path.Length < 4 || path[0] != '/')
            return null;

        var separator = path.IndexOf('/', 1);
        if (separator < 2 || separator == path.Length - 1 || path.IndexOf('/', separator + 1) >= 0)
            return null;

        var service = path[1..separator];
        var method = path[(separator + 1)..];
        if (!_implemented.TryGetValue(service, out var methods))
            return $"unknown service {service}";

        return methods.Contains(method) ? null : $"unknown method {method} for service {service}";
    }

    /// <summary>The middleware: a gRPC call to an unknown service or method gets the trailers-only answer.</summary>
    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.ContentType?.StartsWith(GrpcContentType, StringComparison.OrdinalIgnoreCase) == true
         && HttpMethods.IsPost(context.Request.Method)
         && GetError(context.Request.Path.Value ?? string.Empty) is { } error)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = GrpcContentType;
            context.Response.Headers["grpc-status"] = ((int)StatusCode.Unimplemented).ToString();
            context.Response.Headers["grpc-message"] = PercentEncode(error);
            await context.Response.CompleteAsync();
            return;
        }

        await next(context);
    }

    /// <summary>gRPC's <c>grpc-message</c> encoding: bytes outside printable ASCII and '%' as %XX.</summary>
    private static string PercentEncode(string message)
    {
        var builder = new System.Text.StringBuilder(message.Length);
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(message))
        {
            if (b is >= 0x20 and <= 0x7E && b != (byte)'%')
                builder.Append((char)b);
            else
                builder.Append('%').Append(b.ToString("X2"));
        }

        return builder.ToString();
    }

    /// <summary>The generated <c>ServiceDescriptor</c> of a service implementation's base class.</summary>
    private static ServiceDescriptor? FindDescriptor(Type type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.DeclaringType?.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)
                    ?.GetValue(null) is ServiceDescriptor descriptor)
                return descriptor;
        }

        return null;
    }
}