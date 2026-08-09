<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Notification

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `GET /api/notification/dev/logs/{orderId:guid}` | 1 | `src/Modules/Notification/ModularCommerce.Notification.Api/Endpoints/NotificationDevEndpoints.cs:18` | [diyagram](../flows/get-api-notification-dev-logs-orderid-guid.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `notification.notification_logs` | WR | `Channel`, `Id`, `IdempotencyKey`, `OrderId`, `Recipient`, `SentAtUtc`, `Subject` | `src/Modules/Notification/ModularCommerce.Notification.Infrastructure/Persistence/Configurations/NotificationLogConfiguration.cs:11` |
| `notification.processed_messages` | WR | `ConsumerType`, `IdempotencyKey`, `MessageId`, `ProcessedOnUtc` | `src/Modules/Notification/ModularCommerce.Notification.Infrastructure/Persistence/Configurations/ProcessedMessageConfiguration.cs:14` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

yok.

**Bu modüle dokunanlar:**

- `Ordering` — event, 1 çağrı<br>  `OrderPaid -> OrderPaidNotificationConsumer.Consume (src/Modules/Notification/ModularCommerce.Notification.Api/Consumers/OrderPaidNotificationConsumer.cs:10)`

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.
