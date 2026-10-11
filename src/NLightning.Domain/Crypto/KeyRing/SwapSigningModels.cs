namespace NLightning.Domain.Crypto.KeyRing;

using Models;

public sealed record SwapPrevOutput(long Value, byte[] Script);
public sealed record SwapSignDescriptor(KeyRingLocator? Locator, byte[] PublicKey, int InputIndex, int SignMethod,
                                         uint Sighash, SwapPrevOutput Output, byte[] WitnessScript,
                                         byte[] SingleTweak, byte[] DoubleTweak, byte[] TapTweak);
public sealed record SwapMusigSession(byte[] Id, MusigKeyAggregate Aggregate, byte[] PublicNonce, bool HaveAllNonces);