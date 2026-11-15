using System.Buffers.Binary;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;

namespace Acme.Notes.Api.IntegrationTests;

/// <summary>
/// A software passkey authenticator: answers WebAuthn creation and request options the way a browser
/// and a platform authenticator would (ES256 key, "none" attestation, user present and verified).
/// </summary>
internal sealed class TestAuthenticator : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private byte[] _userHandle = [];
    private uint _signCount;

    public string CredentialId => WebEncoders.Base64UrlEncode(_credentialId);

    /// <summary>The JSON of <c>navigator.credentials.create()</c> for these options.</summary>
    public string Create(string creationOptionsJson, string origin, string? rpIdOverride = null)
    {
        var options = JsonNode.Parse(creationOptionsJson)!;
        var rpId = rpIdOverride ?? options["rp"]!["id"]!.GetValue<string>();
        _userHandle = WebEncoders.Base64UrlDecode(options["user"]!["id"]!.GetValue<string>());
        var clientData = ClientData("webauthn.create", options["challenge"]!.GetValue<string>(), origin);

        var authenticatorData = AuthenticatorData(rpId, attestedCredential: true);
        var attestation = new CborWriter();
        attestation.WriteStartMap(3);
        attestation.WriteTextString("fmt");
        attestation.WriteTextString("none");
        attestation.WriteTextString("attStmt");
        attestation.WriteStartMap(0);
        attestation.WriteEndMap();
        attestation.WriteTextString("authData");
        attestation.WriteByteString(authenticatorData);
        attestation.WriteEndMap();

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = WebEncoders.Base64UrlEncode(clientData),
            ["attestationObject"] = WebEncoders.Base64UrlEncode(attestation.Encode()),
            ["transports"] = new JsonArray("internal"),
        });
    }

    /// <summary>The JSON of <c>navigator.credentials.get()</c> for these options.</summary>
    public string Get(string requestOptionsJson, string origin)
    {
        var options = JsonNode.Parse(requestOptionsJson)!;
        var clientData = ClientData("webauthn.get", options["challenge"]!.GetValue<string>(), origin);
        var authenticatorData = AuthenticatorData(options["rpId"]!.GetValue<string>(), attestedCredential: false);
        var signature = _key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

        return Credential(new JsonObject
        {
            ["clientDataJSON"] = WebEncoders.Base64UrlEncode(clientData),
            ["authenticatorData"] = WebEncoders.Base64UrlEncode(authenticatorData),
            ["signature"] = WebEncoders.Base64UrlEncode(signature),
            ["userHandle"] = WebEncoders.Base64UrlEncode(_userHandle),
        });
    }

    public void Dispose() => _key.Dispose();

    private string Credential(JsonObject response) => new JsonObject
    {
        ["id"] = CredentialId,
        ["rawId"] = CredentialId,
        ["type"] = "public-key",
        ["authenticatorAttachment"] = "platform",
        ["clientExtensionResults"] = new JsonObject(),
        ["response"] = response,
    }.ToJsonString();

    private static byte[] ClientData(string type, string challenge, string origin) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type, challenge, origin, crossOrigin = false }));

    private byte[] AuthenticatorData(string rpId, bool attestedCredential)
    {
        const byte UserPresent = 0x01, UserVerified = 0x04, AttestedCredentialData = 0x40;
        var data = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rpId)))
        {
            (byte)(UserPresent | UserVerified | (attestedCredential ? AttestedCredentialData : 0)),
        };
        var signCount = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(signCount, ++_signCount);
        data.AddRange(signCount);

        if (attestedCredential)
        {
            data.AddRange(new byte[16]); // AAGUID
            var length = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)_credentialId.Length);
            data.AddRange(length);
            data.AddRange(_credentialId);
            data.AddRange(CosePublicKey());
        }

        return [.. data];
    }

    private byte[] CosePublicKey()
    {
        var point = _key.ExportParameters(includePrivateParameters: false).Q;
        var cose = new CborWriter();
        cose.WriteStartMap(5);
        cose.WriteInt32(1); // kty: EC2
        cose.WriteInt32(2);
        cose.WriteInt32(3); // alg: ES256
        cose.WriteInt32(-7);
        cose.WriteInt32(-1); // crv: P-256
        cose.WriteInt32(1);
        cose.WriteInt32(-2); // x
        cose.WriteByteString(point.X!);
        cose.WriteInt32(-3); // y
        cose.WriteByteString(point.Y!);
        cose.WriteEndMap();
        return cose.Encode();
    }
}
