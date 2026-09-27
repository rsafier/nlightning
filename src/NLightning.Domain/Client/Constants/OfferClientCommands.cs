using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Client.Constants;

using Enums;

/// <summary>
/// The provisional <see cref="ClientCommand"/> values of the BOLT 12 offer commands (wave B12, lane B12-D).
/// </summary>
/// <remarks>
/// Lanes never edit <c>ClientCommand.cs</c> (append-only, numbers assigned by the integrator): the integrator adds
/// <c>CreateOffer = 26</c>, <c>ListOffers = 27</c> and <c>DisableOffer = 28</c> there (or the numbers it assigns) and
/// replaces these constants with them.
/// </remarks>
[ExcludeFromCodeCoverage]
public static class OfferClientCommands
{
    /// <summary><c>createoffer</c>.</summary>
    public const ClientCommand CreateOffer = (ClientCommand)26;

    /// <summary><c>listoffers</c>.</summary>
    public const ClientCommand ListOffers = (ClientCommand)27;

    /// <summary><c>disableoffer</c>.</summary>
    public const ClientCommand DisableOffer = (ClientCommand)28;
}