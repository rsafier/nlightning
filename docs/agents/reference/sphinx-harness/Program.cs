using System.Security.Cryptography;
using System.Text.Json;
using LNBolt;
using NBitcoin;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Parameters;

static string H(byte[] b) => Convert.ToHexString(b).ToLower();
static byte[] B(string s) => Convert.FromHexString(s);
static byte[] Hm(string k, byte[] d) { using var h = new HMACSHA256(System.Text.Encoding.ASCII.GetBytes(k)); return h.ComputeHash(d); }
static byte[] Stream(byte[] key, int len) { var e = new ChaCha7539Engine(); e.Init(true, new ParametersWithIV(new KeyParameter(key), new byte[12])); var o = new byte[len]; e.ProcessBytes(new byte[len], 0, len, o, 0); return o; }

var j = JsonDocument.Parse(File.ReadAllText("../onion-test.json")).RootElement;
var gen = j.GetProperty("generate");
var session = B(gen.GetProperty("session_key").GetString());
var ad = B(gen.GetProperty("associated_data").GetString());
var hops = gen.GetProperty("hops").EnumerateArray().Select(h => (pk: B(h.GetProperty("pubkey").GetString()), pl: B(h.GetProperty("payload").GetString()))).ToList();
var privs = j.GetProperty("decode").EnumerateArray().Select(x => B(x.GetString())).ToList();
var expectedOnion = j.GetProperty("onion").GetString();

// ---------- Reference (spec) ----------
var refSS = new List<byte[]>(); var refEph = new List<byte[]>();
var e = new Key(session);
foreach (var h in hops) {
  var epk = e.PubKey.ToBytes(); refEph.Add(epk);
  var ss = SHA256.HashData(new PubKey(h.pk).GetSharedPubkey(e).ToBytes());
  refSS.Add(ss);
  var bf = SHA256.HashData(epk.Concat(ss).ToArray());
  var N = System.Numerics.BigInteger.Parse("0FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141", System.Globalization.NumberStyles.HexNumber);
  var prod = (new System.Numerics.BigInteger(e.ToBytes(), true, true) * new System.Numerics.BigInteger(bf, true, true)) % N;
  var pb = prod.ToByteArray(true, true); var buf = new byte[32]; pb.CopyTo(buf, 32 - pb.Length); e = new Key(buf);
}
// filler
int L = 1300; var sizes = hops.Select(h => h.pl.Length + 32).ToList();
var filler = new byte[0];
for (int i = 0; i < hops.Count - 1; i++) {
  var start = L - filler.Length;
  filler = filler.Concat(new byte[sizes[i]]).ToArray();
  var st = Stream(Hm("rho", refSS[i]), 2 * L);
  for (int x = 0; x < filler.Length; x++) filler[x] ^= st[start + x];
}
var layers = new byte[hops.Count][]; var mix = Stream(Hm("pad", session), L); var hmac = new byte[32];
for (int i = hops.Count - 1; i >= 0; i--) {
  var shifted = new byte[L]; Array.Copy(mix, 0, shifted, sizes[i], L - sizes[i]);
  hops[i].pl.CopyTo(shifted, 0); hmac.CopyTo(shifted, hops[i].pl.Length);
  var st = Stream(Hm("rho", refSS[i]), L); for (int x = 0; x < L; x++) shifted[x] ^= st[x];
  if (i == hops.Count - 1) filler.CopyTo(shifted, L - filler.Length);
  mix = shifted; hmac = new HMACSHA256(Hm("mu", refSS[i])).ComputeHash(mix.Concat(ad).ToArray()); layers[i]=mix.ToArray();
}
{ // reference peel of vector
  var cur = B(expectedOnion)[34..1334];
  for (int i=0;i<hops.Count;i++){ int dd=0; while(dd<L && cur[dd]==layers[i][dd]) dd++; Console.WriteLine($"layer {i} first diff {dd}");
    var st = Stream(Hm("rho", refSS[i]), 2*L); var ext = cur.Concat(new byte[L]).ToArray(); for(int x=0;x<2*L;x++) ext[x]^=st[x];
    cur = ext[sizes[i]..(sizes[i]+L)]; }
}
var refOnion = new byte[] { 0 }.Concat(refEph[0]).Concat(mix).Concat(hmac).ToArray();
Console.WriteLine($"[ref] onion matches test vector: {H(refOnion) == expectedOnion}");

// ---------- LNBolt shared secrets ----------
var lnSS = LNTools.CalculatedSharedSecrets(session, hops.Select(h => h.pk).ToList());
for (int i = 0; i < hops.Count; i++) Console.WriteLine($"[LNBolt] shared secret {i} ok: {H(lnSS[i]) == H(refSS[i])}");
// hop-side derivation
for (int i = 0; i < hops.Count; i++) Console.WriteLine($"[LNBolt] DeriveSharedSecret(eph{i}, priv{i}) ok: {H(LNTools.DeriveSharedSecret(refEph[i], privs[i])) == H(refSS[i])}");
// uncompressed ephemeral pubkey input
var unc = new PubKey(refEph[0]).Decompress().ToBytes();
Console.WriteLine($"[LNBolt] DeriveSharedSecret(uncompressed eph0) ok: {H(LNTools.DeriveSharedSecret(unc, privs[0])) == H(refSS[0])}");
// blinded key length
int bad = 0; var rnd = new Random(1);
for (int t = 0; t < 2000; t++) { var k = new byte[32]; rnd.NextBytes(k); k[0] &= 0x7f; var bf = new byte[32]; rnd.NextBytes(bf); var r = LNTools.GenerateBlindedSessionKey(k, bf); if (r.Length != 32) bad++; }
Console.WriteLine($"[LNBolt] GenerateBlindedSessionKey non-32-byte outputs in 2000 trials: {bad}");

// ---------- LNBolt Peel via private key (as a real hop) ----------
var blob = new OnionBlob(B(expectedOnion));
try {
  var (p0, next0) = blob.Peel(hopPrivateKey: privs[0], associatedData: ad);
  Console.WriteLine($"[LNBolt] peel0 ok, amt={p0.AmountToForward} cltv={p0.OutgoingCltvValue}");
  Console.WriteLine($"[LNBolt] next ephemeral key len={next0.EphemeralPublicKey.Length} correct={H(next0.EphemeralPublicKey) == H(refEph[1])}");
  Console.WriteLine($"[LNBolt] original blob mutated by Peel: {H(blob.EphemeralPublicKey) != H(refEph[0])}");
  try { next0.Peel(hopPrivateKey: privs[1], associatedData: ad); Console.WriteLine("[LNBolt] peel1 via privkey ok"); }
  catch (Exception ex) { Console.WriteLine($"[LNBolt] peel1 via privkey FAILED: {ex.GetType().Name}: {ex.Message}"); }
} catch (Exception ex) { Console.WriteLine($"[LNBolt] peel0 FAILED: {ex.GetType().Name}: {ex.Message}"); }

// ---------- LNBolt Peel via externally supplied shared secrets (LND DeriveSharedKey path) ----------
blob = new OnionBlob(B(expectedOnion));
for (int i = 0; i < hops.Count; i++) {
  try {
    var (p, n) = blob.Peel(sharedSecret: refSS[i], associatedData: ad);
    Console.WriteLine($"[LNBolt] peel{i} with given ss: amt={p.AmountToForward} cltv={p.OutgoingCltvValue} sphinxSize={p.SphinxSize} expected={hops[i].pl.Length} otherTLVs={p.OtherTLVs.Count} final={(n == null)}");
    if (n == null) break; blob = n;
  } catch (Exception ex) { Console.WriteLine($"[LNBolt] peel{i} with given ss FAILED: {ex.GetType().Name}: {ex.Message}"); break; }
}

// ---------- LNBolt ConstructOnion for simple TLV payloads vs reference ----------
var simple = new List<HopPayload>();
for (int i = 0; i < 3; i++) simple.Add(new HopPayload { HopPayloadType = HopPayloadType.TLV, AmountToForward = 1000UL + (ulong)i, OutgoingCltvValue = 100u + (uint)i, ChannelId = Enumerable.Repeat((byte)(i+1), 8).ToArray() });
var ss3 = refSS.Take(3).ToList();
var ln = OnionBlob.ConstructOnion(ss3, simple, refEph[0], ad);
Console.WriteLine($"[LNBolt] ToDataBuffer len={simple[0].ToDataBuffer().Length} SphinxSize={simple[0].SphinxSize} ToSphinxBuffer len={simple[0].ToSphinxBuffer().Length}");
// peel LNBolt-constructed onion with reference logic
try { var (pp, nn) = ln.Peel(sharedSecret: ss3[0], associatedData: ad); Console.WriteLine($"[LNBolt] self-peel of constructed onion: amt={pp.AmountToForward} cltv={pp.OutgoingCltvValue}"); }
catch (Exception ex) { Console.WriteLine($"[LNBolt] self-peel of constructed onion FAILED: {ex.GetType().Name}: {ex.Message}"); }
var ro = H(refOnion); int d = 0; while (d < ro.Length && ro[d] == expectedOnion[d]) d++;
Console.WriteLine($"first diff hex idx {d} (byte {d/2}) of {ro.Length}, lens {ro.Length} {expectedOnion.Length}");
{
  // final hop direct: layer 4 with hmac of layer 4 computed by reference
  var hm4 = new HMACSHA256(Hm("mu", refSS[3])); // placeholder not used
  // recompute hmac for layer4: stored in layer3 plaintext; derive via reference peel of layer3
  var st = Stream(Hm("rho", refSS[3]), 2*L); var ext = layers[3].Concat(new byte[L]).ToArray(); for(int x=0;x<2*L;x++) ext[x]^=st[x];
  var nh = ext[(sizes[3]-32)..sizes[3]];
  var fb = new OnionBlob(0, refEph[4], layers[4], nh);
  try { var (p, n) = fb.Peel(sharedSecret: refSS[4], associatedData: ad);
    Console.WriteLine($"[LNBolt] final hop: amt={p.AmountToForward} cltv={p.OutgoingCltvValue} paymentSecret={(p.PaymentData==null?"null":H(p.PaymentData.PaymentSecret))} total={p.PaymentData?.TotalMSat} other={string.Join(",",p.OtherTLVs.Select(t=>t.Type))} sphinxSize={p.SphinxSize} expected={hops[4].pl.Length} detectedFinal={(n==null)}"); }
  catch (Exception ex) { Console.WriteLine($"[LNBolt] final hop FAILED {ex.GetType().Name}: {ex.Message}"); }
  // filler compare for simple payloads
  var sz = simple.Select(x => x.SphinxSize + 32).ToList(); var f = new byte[0];
  for (int i = 0; i < 2; i++) { var start = L - f.Length; f = f.Concat(new byte[sz[i]]).ToArray(); var s2 = Stream(Hm("rho", ss3[i]), 2*L); for (int x=0;x<f.Length;x++) f[x]^=s2[start+x]; }
  Console.WriteLine($"[LNBolt] GenerateFiller matches spec filler: {H(OnionBlob.GenerateFiller(ss3, simple)) == H(f)}");
  Console.WriteLine($"[LNBolt] Rho/Mu/Um key derivation: {H(LNTools.GenerateRhoKey(refSS[0]))==H(Hm("rho",refSS[0]))} {H(LNTools.GenerateMuKey(refSS[0]))==H(Hm("mu",refSS[0]))} {H(LNTools.GenerateUmKey(refSS[0]))==H(Hm("um",refSS[0]))}");
  Console.WriteLine($"[LNBolt] ChaCha stream matches: {H(LNTools.GenerateCipherStream(new byte[L], Hm("rho",refSS[0]), new byte[12]))==H(Stream(Hm("rho",refSS[0]),L))}");
}
