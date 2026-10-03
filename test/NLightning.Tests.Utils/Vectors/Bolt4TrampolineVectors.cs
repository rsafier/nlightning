using System.Diagnostics.CodeAnalysis;

namespace NLightning.Tests.Utils.Vectors;

/// <summary>
/// Trampoline hop payloads of BOLTs PR 836 (head 8f5f37a8), <c>bolt04/trampoline-payment-onion-test.json</c> and
/// <c>bolt04/trampoline-to-blinded-path-payment-onion-test.json</c>, with their bigsize length prefix (as the
/// vectors give them), plus the trampoline onion Alice built.
/// </summary>
/// <remarks>
/// Also compiled into <c>NLightning.Infrastructure.Serialization.Tests</c> as a linked file (that project does not
/// reference Tests.Utils).
/// </remarks>
[ExcludeFromCodeCoverage]
public static class Bolt4TrampolineVectors
{
    /// <summary>trampoline-payment-onion-test.json, Alice's trampoline payload for Carol (2, 4, 14).</summary>
    public const string IntermediateInner = "2e020405f5e10004030c35000e2102edabbd16b41c8371b92ef2f04c1185b4f03b6dcd52ba9b78d9d7c89c8f221145";

    /// <summary>trampoline-payment-onion-test.json, Alice's trampoline payload for Eve (2, 4, 8).</summary>
    public const string FinalInner = "31020405f5e10004030c350008242a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a2a05f5e100";

    /// <summary>trampoline-payment-onion-test.json, Alice's outer payload for Carol (2, 4, 8, 20).</summary>
    public const string OuterWithTrampoline =
        "fd0116020405f5f48804030c35fa08242b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b2b05f5"
      + "f48814e30002531fe6068134503d2723133227c867ac8fa6c83c537e9a44c3c5bdbdcb1fe3371860c0749bfd613056cfc571"
      + "8beecc25a2f255fc7abbea3cd75ff820e9d30807d19b30f33626452fa54bb2d822e918558ed3e6714deb3f9a2a10895e7553"
      + "c6f088c9a852043530dbc9abcc486030894364b205f5de60171b451ff462664ebce23b672579bf2a444ebfe0a81875c26d2f"
      + "a16d426795b9b02ccbc4bdf909c583f0c2ebe9136510645917153ecb05181ca0c1b207824578ee841804a148f4c3df7306dc"
      + "ea52d94222907c9187bc31c0880fc084f0d88716e195c0abe7672d15217623";

    /// <summary>trampoline-payment-onion-test.json, the trampoline onion Alice built.</summary>
    public const string AliceTrampolineOnion =
        "0002531fe6068134503d2723133227c867ac8fa6c83c537e9a44c3c5bdbdcb1fe3371860c0749bfd613056cfc5718beecc25"
      + "a2f255fc7abbea3cd75ff820e9d30807d19b30f33626452fa54bb2d822e918558ed3e6714deb3f9a2a10895e7553c6f088c9"
      + "a852043530dbc9abcc486030894364b205f5de60171b451ff462664ebce23b672579bf2a444ebfe0a81875c26d2fa16d4267"
      + "95b9b02ccbc4bdf909c583f0c2ebe9136510645917153ecb05181ca0c1b207824578ee841804a148f4c3df7306dcea52d942"
      + "22907c9187bc31c0880fc084f0d88716e195c0abe7672d15217623";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [0], the payer's trampoline payload for Carol (2, 4, 21, 22).</summary>
    public const string ToBlindedPathsInner =
        "fd01b5020408f0d18004030c3500150302000016fd01a1032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7"
      + "b1e66868099102988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c5f76087b960e020295d40514096a8be548"
      + "59e7dfe947b376eaafea8afe5cb4eb2c13ff857ed0b4be002b0ae636dc5963bcfe2a4705538b3b6d2c5cd87dce29374d47cb"
      + "64d16b3a0d95f21b1af81f31f61c01e81a86020e2dbadcc2005e859819ddebbe88a834ae8a6d2b049233c07335f15cd1dc5f"
      + "2200d1bcd747ba974bc6ac175df8d5dbd462acb1dc4f3fa1de21da4c5774d233d8ecd9b84b7420175f9ec920f2ef261cdb83"
      + "dc28cc3a0eeb970107b3306489bf771ef5b1213bca811d345285405861d08a655b6c237fa247a8b4491beee20c878a60e981"
      + "6492026d8feb9dafa84585b253978db6a0aa2945df5ef445c61e801fb82f43d59347cc1c013a2351f094cdafb5e0d1f5ccb1"
      + "055d6a5dd086a69cd75d34ea06067659cb7bb02dda9c2d89978dc725168f93ab2fe22dff354bce6017b60d0cc5b29b015405"
      + "95e6d024f3812adda1960b4d000001f4000003e800240000000000000001000000001dcd65000000";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Carol's trampoline payload (2, 4, 14).</summary>
    public const string BlindedIntermediateTrampolineInner = "2e020408f31d6404030c35240e21032c0b7cf95324a07d05398b240174dc0c2be444d96b159aa6c7f7b1e668680991";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Dave's trampoline payload (10, 12).</summary>
    public const string BlindedIntroductionInner =
        "690a440ccf3c8a58deaa603f657ee2a5ed9d604eb5c8ca1e5f801989afa8f3ea6d789bbdde2c7e7a1ef9ca8c38d2c54760fe"
      + "bad8446d3f273ddb537569ef56613846ccd3aba78a0c2102988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c"
      + "5f76087b960e";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], blinded(Eve)'s trampoline payload (2, 4, 10, 18).</summary>
    public const string BlindedFinalInner =
        "e4020408f0d18004030c35000ad1bcd747394fbd4d99588da075a623316e15a576df5bc785cccc7cd6ec7b398acce6faf520"
      + "175f9ec920f2ef261cdb83dc28cc3a0eeb970107b3306489bf771ef5b1213bca811d345285405861d08a655b6c237fa247a8"
      + "b4491beee20c878a60e9816492026d8feb9dafa84585b253978db6a0aa2945df5ef445c61e801fb82f43d5f00716baf9fc9b"
      + "3de50bc22950a36bda8fc27bfb1242e5860c7e687438d4133e058770361a19b6c271a2a07788d34dccc27e39b9829b061a4d"
      + "960eac4a2c2b0f4de506c24f9af3868c0aff6dda27281c120408f0d180";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Dave's outer payload for Eve (2, 4, 8, 12, 20).</summary>
    public const string BlindedFinalOuter =
        "fd0278020408f0d18004030c350008241221f15a9dece128347dac673d6171be13b3d92c9c77ff581506507045a1d2e808f0"
      + "d1800c2102c952268f1501cf108839f4f5d0fbb41a97de778a6ead8caf161c569bd4df1ad714fd022000038da50a45c30a08"
      + "6668ad1c34a23c11ee94bf0b4e7b8b8b184b7914645aef9e1ecf8f95df787c87965210644a84d1da8baf1731a02d0a292ae9"
      + "c6e685a36e0e1679a8e0c38c27de47014966aaacfea446571ddaf7afcff7c3517e7bf57f87388720a1f226cc9ba1f6703964"
      + "35163a6872d39d2460adafdefb355bc5a89d51e62d427aac45e40b18d2b34587ca19753a8a0a7d704e38c190034b0c5b253b"
      + "d566e20845a22e81d2d6a74071dfdfefe6fceb555f3d52a7f7d6b99e8e74a6cf4893f7374b473e28e62c9d99fc386ed220dd"
      + "0ecc50274883d9f6a63e4aabdc1d6604827367dd3b3ddf233c2a8a7d577bf75736ca77c5d7d43f85db51c7cc6e3351322542"
      + "8e525ac0c22f6ef6c509e4ebbe4074f1fb726a8fd1e8643893e9fa38ae1eb6fd761e7fb12db8d3f20b5b26483b3fb92e6eb9"
      + "fabd647870ddc39d61de48bdc39ce26eedf2f4d8da60adc13876844ddda3cc902792a8bd113980011279cddc625b9bcda8b0"
      + "cc91cacaa4061d565a0b6e5daecf21ef3ce1be4d195c28ddc7337754e1d58908c4d8ffb45d0fbe936b83beb9851b88e57026"
      + "c80e3e6d7b5b984785b4dd67498f86a9afcfc0548837b87ce07ef524696b68dc5a42312588dd051ea608f46dec1613c558e1"
      + "1d64e32c5cfd6b0e1c93691c724b257033d93dc7fffebca7f494d2b6391492985eac16d6919dcf60f1ab49e6ae216c90776b"
      + "48ace0404128313220af7b6e546d1b89ab356cab83059301ae2d3a0eff524a610649c8";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Eve's blinded path: the first path key.</summary>
    public const string BlindedPathKey =
        "02988face71e92c345a068f740191fd8e53be14f0bb957ef730d3c5f76087b960e";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Eve's blinded path: Dave's encrypted_recipient_data.</summary>
    public const string BlindedDaveEncryptedData =
        "0ccf3c8a58deaa603f657ee2a5ed9d604eb5c8ca1e5f801989afa8f3ea6d789bbdde2c7e7a1ef9ca8c38d2c54760febad844"
      + "6d3f273ddb537569ef56613846ccd3aba78a";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Eve's blinded path: Dave's blinded node id.</summary>
    public const string BlindedDaveBlindedNodeId =
        "0295d40514096a8be54859e7dfe947b376eaafea8afe5cb4eb2c13ff857ed0b4be";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Eve's blinded path: Eve's encrypted_recipient_data.</summary>
    public const string BlindedEveEncryptedData =
        "bcd747394fbd4d99588da075a623316e15a576df5bc785cccc7cd6ec7b398acce6faf520175f9ec920f2ef261cdb83dc28cc"
      + "3a0eeb970107b3306489bf771ef5b1213bca811d345285405861d08a655b6c237fa247a8b4491beee20c878a60e981649202"
      + "6d8feb9dafa84585b253978db6a0aa2945df5ef445c61e801fb82f43d5f00716baf9fc9b3de50bc22950a36bda8fc27bfb12"
      + "42e5860c7e687438d4133e058770361a19b6c271a2a07788d34dccc27e39b9829b061a4d960eac4a2c2b0f4de506c24f9af3"
      + "868c0aff6dda27281c";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], Eve's blinded path: Eve's blinded node id.</summary>
    public const string BlindedEveBlindedNodeId =
        "020e2dbadcc2005e859819ddebbe88a834ae8a6d2b049233c07335f15cd1dc5f22";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], the payer's trampoline session key.</summary>
    public const string BlindedTrampolineSessionKey =
        "a64feb81abd58e473df290e9e1c07dc3e56114495cadf33191f44ba5448ebe99";

    /// <summary>trampoline-to-blinded-path-payment-onion-test.json [1], the payer's trampoline onion (Carol, Dave, blinded(Eve)).</summary>
    public const string BlindedTrampolineOnion =
        "0002bc59a9abc893d75a8d4f56a6572f9a3507323a8de22abe0496ea8d37da166a8b4bba0e560f1a9deb602bfd98fe916714"
      + "1d0b61d669df90c0149096d505b85d3d02806e6c12caeb308b878b6bc7f1b15839c038a6443cd3bec3a94c2293165375555f"
      + "6d7720862b525930f41fddcc02260d197abd93fb58e60835fd97d9dc14e7979c12f59df08517b02e3e4d50e1817de4271df6"
      + "6d522c4e9675df71c635c4176a8381bc22b342ff4e9031cede87f74cc039fca74aa0a3786bc1db2e158a9a520ecb99667ef9"
      + "a6bbfaf5f0e06f81c27ca48134ba2103229145937c5dc7b8ecc5201d6aeb592e78faa3c05d3a035df77628f0be9b1af3ef7d"
      + "386dd5cc87b20778f47ebd40dbfcf12b9071c5d7112ab84c3e0c5c14867e684d09a18bc93ac47d73b7343e3403ef6e3b7036"
      + "6835988920e7d772c3719d3596e53c29c4017cb6938421a557ce81b4bb26701c25bf622d4c69f1359dc85857a375c5c74987"
      + "a4d3152f66987001c68a50c4bf9e0b1dab4ad1a64b0535319bbf6c4fbe4f9c50cb65f5ef887bfb91b0a57c0f86ba3d91cbee"
      + "a1607fb0c12c6c75d03bbb0d3a3019c40597027f5eebca23083e50ec79d41b1152131853525bf3fc13fb0be62c2e3ce733f5"
      + "9671eee5c4064863fb92ae74be9ca68b9c716f9519fd268478ee27d91d466b0de51404de3226b74217d28250ead9d2c95411"
      + "e0230570f547d4cc7c1d589791623131aa73965dccc5aa17ec12b442215ce5d346df664d799190df5dd04a13";
}