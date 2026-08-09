<!-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin. `flowlens docs` ile yeniden üretilir. -->

# Payment

## Endpoint'ler

| Endpoint | Tablo | Tanım | Akış |
|---|---:|---|---|
| `GET /api/payment/dev/payments` | 1 | `src/Modules/Payment/ModularCommerce.Payment.Api/Endpoints/PaymentDevEndpoints.cs:22` | [diyagram](../flows/get-api-payment-dev-payments.md) |

## Tablolar

| Tablo | Erişim | Kolonlar | Tanım |
|---|---|---|---|
| `payment.payment_attempts` | W | `AttemptNumber`, `ErrorCode`, `LatencyMs`, `OccurredAtUtc`, `Outcome`, `PspTransactionId`, `id`, `payment_id` | `src/Modules/Payment/ModularCommerce.Payment.Infrastructure/Persistence/Configurations/PaymentConfiguration.cs:56` |
| `payment.payments` | WR | `Amount`, `ClaimedAtUtc`, `CompletedAtUtc`, `CreatedAtUtc`, `Currency`, `CustomerId`, `FailureCode`, `Id`, `IdempotencyKey`, `Method`, `OrderId`, `PspTransactionId`, `RefundTransactionId`, `RefundedAtUtc`, `Status` | `src/Modules/Payment/ModularCommerce.Payment.Infrastructure/Persistence/Configurations/PaymentConfiguration.cs:14` |

`W` = yazılıyor · `R` = okunuyor · kolonlar yalnız bir yazma onları adlandırdığında listelenir.

## Event'ler

Bu modül integration event tanımlamıyor.

## Bağımlılıklar

**Bu modülün dokunduğu modüller:**

yok.

**Bu modüle dokunanlar:**

- `Ordering` — sözleşme, 2 çağrı<br>  `CancelOrderHandler.HandleAsync -> IPaymentService.RefundAsync (src/Modules/Payment/ModularCommerce.Payment.Contracts/IPaymentService.cs:7)`

## Bilinen sınırlar

Bu sayfa için kaydedilmiş bir sınır yok.
