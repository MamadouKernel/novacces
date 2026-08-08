namespace NovAcces.Shared.Dtos;

/// <summary>Clé publique VAPID — nécessaire au navigateur pour créer un abonnement WebPush.</summary>
public sealed record VapidPublicKeyDto(string PublicKey);

/// <summary>Miroir de PushSubscriptionJSON (spec Push API navigateur, `subscription.toJSON()`).</summary>
public sealed record PushSubscriptionKeysDto(string P256dh, string Auth);

public sealed record PushSubscriptionRequestDto(string Endpoint, PushSubscriptionKeysDto Keys);

public sealed record PushUnsubscribeRequestDto(string Endpoint);

/// <summary>Jeton de notification push Expo (app agent React Native/Expo).</summary>
public sealed record AgentPushTokenRequestDto(string ExpoPushToken);
