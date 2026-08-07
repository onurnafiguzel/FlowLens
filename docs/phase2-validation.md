# Faz 2 Doğrulaması

> Ölçüm tarihi: 2026-08-07 · Hedef: `ModularCommerce.sln` (66 proje, 48'i test dışı) · SDK 10.0.301
> Yöntem: FlowLens çıktısı, ModularCommerce kaynak kodu **elle okunarak** karşılaştırıldı.

---

## 0. Verdict kriterleri — rapordan ÖNCE tanımlandı

Ölçütü sonuca göre uydurmamak için dört kriter, ölçüm yapılmadan önce sabitlendi:

| # | Kriter | Sonuç |
|---|---|---|
| 1 | Checkout ve catalog zincirlerinde **atlanan in-source çağrı = 0** | ✅ (§5, §6 — kaveatlı, aşağıda) |
| 2 | OrderPaid köprüsünün **üç halkası da** kaynakta teyitli | ✅ (§7) |
| 3 | **Truncation = 0** | ✅ (§3) |
| 4 | Bulunan boşlukların tamamı sınıflandırılmış ve düzeltilebilir olanların Faz 3'e etkisi değerlendirilmiş | ✅ (§9) |

**Verdict: Faz 3'e geçilebilir.** Gerekçe ve kaveatlar §10'da.

---

## 1. Endpoint sayısı — uyuşmazlık yok

Rapor "survey 24 diyor, FlowLens 25 buldu" diye bir çelişki bırakmıştı. İki sayı **farklı şeyleri
sayıyor**:

| Kaynak | Modül endpoint'i | Modül dışı | Toplam |
|---|---:|---|---:|
| Survey §2.2 tablosu | 24 | `GET /` + `/health/live` + `/health/ready` (§2.2 notu) | **27** |
| FlowLens | **24** | `GET /` | **25** |

Modül dağılımı **birebir** örtüşüyor:

| Modül | Survey | FlowLens |
|---|---:|---:|
| Cart | 4 | 4 |
| Catalog | 4 | 4 |
| Discovery | 1 | 1 |
| Identity | 4 | 4 |
| Inventory | 5 | 5 |
| Notification | 1 | 1 |
| Ordering | 4 | 4 |
| Payment | 1 | 1 |
| Shipping | 0 | 0 |
| **Toplam** | **24** | **24** |

**Sonuç: ne survey yanlıştı ne FlowLens fazla buldu.** 25 = 24 modül + `Program.cs:79`'daki
`GET /`. Kapsama **25/27 (%92,6)**.

Eksik olan 2 endpoint `MapHealthChecks` ile kaydediliyor
([`HealthCheckExtensions.cs:30,37`](../../ModularCommerce/src/Shared/ModularCommerce.Shared.Infrastructure/Observability/HealthCheckExtensions.cs))
→ **L7**. Sessiz kayıp değil: CLI 25 bulduğunu ve baseline'ın 24 olduğunu birlikte basıyor.

> **Faz 1'deki "68 proje" durumundan farkı:** orada survey gerçekten yanlıştı (66'ydı) ve
> düzeltildi. Burada iki doğru sayı farklı kümeleri sayıyordu; düzeltilecek bir şey yok,
> netleştirilecek bir şey vardı.

### MapX invocation muhasebesi

```
25 endpoints · 0 unresolved route · 0 candidates eliminated · 0 multi-mount
pass 1: 25 map calls, 11 prefix propagations · pass 2: 19 methods reached
```

| Aşama | Sayı |
|---|---:|
| Adı bir Map fiiliyle eşleşen invocation | 25 |
| Sembol doğrulamasından geçen | **25** |
| Elenen | **0** |
| Route'u çözülemeyen | **0** |

ModularCommerce'te custom `MapX` wrapper'ı yok, o yüzden eleme listesi boş. Liste mekanizması
sentetik testle doğrulandı (`RoutePrefixResolverTests` — kullanıcı tanımlı bir `MapPost`
`resolved-to-other-type` sebebiyle eleniyor ve `file:line` ile kaydediliyor).

---

## 2. Invocation istatistikleri

Bu sayılar Faz 2 raporunda yoktu; doğrulama için `TraceStats`'a enstrümantasyon eklendi.

| Metrik | Checkout | Catalog |
|---|---:|---:|
| **Gezilen invocation** | **369** | **25** |
| `GetSymbolInfo().Symbol` ile çözülen | 369 | 25 |
| `CandidateSymbols`'a düşen | **0** | **0** |
| Çözülemeyen | **0** | **0** |
| Framework/NuGet filtresi (çözüldü, solution dışı) | 154 | 18 |
| Interface üyesine bağlanan | 28 | 2 |
| `ambiguous` işaretli düğüm | 12 | 2 |
| `truncated` düğüm | **0** | **0** |
| `SymbolFinder` çağrısı / cache isabeti | 23 / 6 | 2 / 1 |
| Düğüm / kenar | 106 / 185 | 9 / 9 |

**Çözüm oranı %100.** `CandidateSymbols` hiç devreye girmedi — beklenen sonuç: ModularCommerce
0 hata ile derleniyor, ve Faz 1'de öğrenildiği gibi `CandidateSymbols` esasen derleme bozukken
dolar.

**Framework filtresi baskın:** checkout'ta 369 invocation'ın **154'ü (%42)** solution dışı
(`string.Join`, LINQ `Select`, `IValidator.ValidateAsync`, EF Core `SaveChangesAsync`, Polly
`ExecuteAsync`, `Task.Delay`, `Random.Shared.NextDouble`…). Graph proje kodunu tarif ediyor;
bu bilinçli.

### Derinlik dağılımı (Faz 3 traversal maliyeti için)

| Derinlik | Checkout | Catalog |
|---:|---:|---:|
| 0 | 1 | 1 |
| 1 | 3 | 2 |
| 2 | 19 | 4 |
| 3 | 16 | 2 |
| 4 | 14 | — |
| 5 | 20 | — |
| 6 | 14 | — |
| 7 | 10 | — |
| 8 | 7 | — |
| 9 | 1 | — |
| 10 | 1 | — |

Dağılım seviye 5'te tepe yapıp hızla sönüyor. **Faz 3 için anlamı:** `Forward()` BFS'i birkaç yüz
düğümden fazlasını gezmeyecek; roadmap'in "birkaç bin node'da `List` + LINQ milisaniyeler içinde
döner" varsayımı fazlasıyla güvenli.

---

## 3. Derinlik — ölçüldü, varsayılan buna göre ayarlandı

Faz 2 raporundaki checkout çıktısı varsayılan `--max-depth 10` ile alınmıştı ve
`FakePspClient.Roll` `[truncated]` işaretliydi — yani **teslim edilen çıktı "eksik olabilir"
diyordu.** Ölçüm:

| `--max-depth` | Düğüm | Truncated |
|---:|---:|---:|
| 10 | 106 | **1** |
| 11 | 106 | 0 |
| 12 | 106 | 0 |
| 50 | 106 | 0 |

**Bulgu: eski varsayılan hiçbir düğüm kaybetmemişti.** Derinlik 10 ile derinlik 50 **birebir aynı
106 düğümlük** graph'ı üretiyor. Sınırdaki düğüm, genişletilmesinin bir şey ekleyip eklemeyeceği
bilinmeden işaretleniyor — burada eklemiyordu.

Neden eklemiyordu, kaynakta doğrulandı: `FakePspClient.Roll` (`FakePspClient.cs:44`) gövdesi
`rate > 0 && Random.Shared.NextDouble() < rate` — tek çağrısı framework, dolayısıyla in-source
çocuğu yok.

**Bayrak muhafazakârdı, yanlış değildi.** Ama "belki eksik" bir çıktıyı varsayılan yapmak doğru
değil.

- ModularCommerce'in **en uzun zinciri: 10 seviye** (checkout). Catalog: 3.
- Truncation raporlamayan **ilk sınır: 11**.
- **Yeni varsayılan: 20** (~2× pay). `CallGraphWalker.cs` → `TraversalOptions.MaxDepth`.

**Node bütçesi:** sınırsız derinlikte 106 düğüm; bütçe 5000. Aşılma yok, yakın bile değil.

---

## 4. Performans — 3 koşu, ortalama ve aralık

Faz 2 raporu endpoint keşfi için tek koşudan "5,5 s ✘" yazmıştı. Tek ölçüm Windows'ta dosya
cache'ine göre oynar; 3 koşu:

| Aşama | Hedef | Koşu 1 | Koşu 2 | Koşu 3 | **Ortalama** | Aralık | Durum |
|---|---|---:|---:|---:|---:|---|---|
| Solution yükleme | ≤ 20 s | 16,3 | 16,2 | 15,8 | **16,1 s** | 15,8–16,3 | ✅ |
| Endpoint keşfi | ≤ 5 s | 5,0 | 5,0 | 4,9 | **4,97 s** | 4,9–5,0 | ✅ *sınırda* |
| Zincir + SymbolFinder | ≤ 30 s | 0,5 | 0,5 | 0,5 | **0,5 s** | — | ✅ |
| **Toplam** | **≤ 60 s** | 23 | 24 | 22 | **23 s** | 22–24 | ✅ |

**Aksiyon:** raporun "5,5 s ✘" ifadesi **tek koşunun gürültüsüydü** — o koşu build'den hemen sonra,
soğuk dosya cache'iyle alınmıştı. Üç koşunun hiçbiri 5,0 s'yi aşmıyor, ortalama 4,97 s.

**Ama dürüst olmak gerekirse hedef sınırda tutuyor, rahat değil** (%0,6 pay). Aşamanın maliyeti
büyük ölçüde 48 projede ilk `GetSemanticModelAsync` kurulumu; ModularCommerce birkaç modül daha
büyürse bu hedef aşılır. O noktada iki seçenek var: `EndpointDiscovery.MentionsAnyMapVerb`'in
`SyntaxTree.GetText()` ön filtresini ucuzlatmak, ya da hedefi gerekçesiyle revize etmek.
**Şimdilik hedef geçerli ve tutuyor.**

---

## 5. Checkout zinciri — kaynak → trace tam sayım

> **Yön kuralı.** Trace'teki düğümü kaynakta aramak yalnızca **fazlalıkları** gösterir. Eksikleri
> yakalamak için sayım ters kuruldu: kaynak dosyadaki **her** invocation için trace'te karşılığı
> arandı.

### 5.1 `CheckoutHandler.HandleAsync` — 19/19

`CheckoutHandler.cs:23-208`'deki her in-source invocation:

| Kaynak satırı | Çağrı | Trace'te |
|---|---|---|
| :27 | `validator.ValidateAsync` | — *(FluentValidation, framework filtresi)* |
| :30, 47, 60, 72, 88, 109, 118, 127, 145, 152, 188 | `Result.Failure<T>` | ✔ |
| :30 | `Error.Validation` | ✔ |
| :32 | `string.Join`, `Errors.Select` | — *(framework)* |
| :36, 55, 179 | `orders.GetByIdempotencyKeyAsync` | ✔ `IOrderRepository.GetByIdempotencyKeyAsync` |
| :40, 59, 183 | `Replay` | ✔ `CheckoutHandler.Replay` |
| :44 | `cartService.GetItemsAsync` | ✔ `ICartService.GetItemsAsync` |
| :64 | `productReader.GetByIdsAsync` | ✔ `IProductReader.GetByIdsAsync` |
| :65 | `cartLines.Select`, `.ToDictionary` | — *(framework)* |
| :72 | `OrderErrors.ProductUnavailable` | ✔ |
| :82 | `stockReservation.ReserveAsync` | ✔ `IStockReservationService.ReserveAsync` |
| :87, 108, 117, 127, 144, 151, 165, 173 | `ReleaseAllAsync` | ✔ `CheckoutHandler.ReleaseAllAsync` |
| :91, 94 | `reservedIds.Add`, `drafts.Add` | — *(framework, `List<T>.Add`)* |
| :105 | `Order.Create` | ✔ |
| :114 | `order.MarkStockReserved` | ✔ |
| :123 | `order.MarkPaymentPending` | ✔ |
| :133 | `paymentService.ChargeAsync` | ✔ `IPaymentService.ChargeAsync` |
| :148 | `order.MarkPaid` | ✔ |
| :159 | `orders.AddAsync` | ✔ `IOrderRepository.AddAsync` |
| :194 | `CommitAllAsync` | ✔ `CheckoutHandler.CommitAllAsync` |
| :198 | `cartService.ClearAsync` | ✔ `ICartService.ClearAsync` |
| :201 | `logger.LogWarning` | — *(framework)* |
| :207 | `OrderResponse.FromOrder` | ✔ |
| :207, 211 | `Result.Success` | ✔ |

**Atlanan in-source metot çağrısı: 0. Fazladan gelen: 0.**

Metot **olmayan** referanslar (bilerek kenar üretmiyor):
- `OrderErrors.EmptyCart` (:60), `OrderErrors.DuplicateIdempotencyKey` (:175) — `static readonly`
  alan → **L10**. `OrderErrors` sınıfı graph'ta zaten var (`ProductUnavailable` metot çağrısıyla).
- `new CheckoutResponse(...)`, `new OrderLineDraft(...)`, `new ChargeRequest(...)` — constructor
  → **L9**. Üçü de **gövdesiz positional record**; hiçbir kod yolu gizlemiyorlar.

### 5.2 Daha önce kesilen bölge — ödeme yolu

Faz 2 raporundaki karşılaştırma bu bölgeyi hiç görmemişti. Tam çıktıyla:

**`PaymentService.ChargeAsync`** (`PaymentService.cs:27`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :31 | `strategies.FirstOrDefault(...)` | — *(LINQ)* |
| :38 | `PaymentAggregate.Create(...)` | ✔ `Payment.Create` |
| :53 | `context.Payments.Add(...)` | — *(EF Core)* |
| :56 | `context.SaveChangesAsync(...)` | — *(EF Core)* |
| :66 | `HandleExistingPaymentAsync(...)` | ✔ |
| :69 | `ExecuteChargeAsync(...)` | ✔ |

**`CardPaymentStrategy.ExecuteAsync`** (`CardPaymentStrategy.cs:24`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :29 | `pipelineProvider.GetPipeline(...)` | — *(Polly)* |
| :33 | `pipeline.ExecuteAsync(async token => …)` | — *(Polly)* |
| :34 | **lambda içinde** `ChargeOnceAsync(...)` | ✔ `CardPaymentStrategy.ChargeOnceAsync` |
| :38, 39, 45, 50, 56 | `PspChargeOutcome.Success` / `.Failure` | ✔ |
| :42 | `PaymentErrors.Declined(...)` | ✔ |
| :45, 50 | `PaymentErrors.PspUnavailable`, `.Timeout` | — *(`static readonly` alan → L10)* |

**`FakePspClient.ChargeAsync`** (`FakePspClient.cs:15`) ve `Roll` (`:44`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :17, :28, :33 | `Roll(...)` | ✔ `FakePspClient.Roll` |
| :20, :25 | `Task.Delay(...)` | — *(framework)* |
| :30 | `new PspTransientException(...)` | — *(constructor → L9)* |
| :35, :38 | `new PspResult(...)` | — *(constructor → L9, gövdesiz record)* |
| :45 (Roll) | `Random.Shared.NextDouble()` | — *(framework)* |

**Kesilen bölgede de atlanan in-source metot çağrısı: 0.**

### 5.3 Delegate/lambda üzerinden yapılan çağrı — yakalandı, ama mekanizma modellenmedi

`CardPaymentStrategy.ExecuteAsync`, `ChargeOnceAsync`'i **Polly'ye geçirdiği bir lambda içinde**
çağırıyor. FlowLens bunu yakaladı, çünkü `ExpandInvocationsAsync` metot bildiriminin **tüm alt
ağacını** geziyor ve lambda gövdeleri de o ağaçta.

**Sonuç doğru, ama sebep yaklaşık:** FlowLens Polly'nin delegate'i çağırdığını bilmiyor; çağrıyı
metnin içinde gördüğü için ekliyor. İki yan etkisi var:
- Yaratılıp **hiç çağrılmayan** bir lambda da kenar üretir (yanlış pozitif).
- Bir delegate başka metoda geçirilip **orada** çağrılırsa, kenar yanlış çağırana bağlanır.

Bu vakada ikisi de zararsız — Polly delegate'i gerçekten çağırıyor. → **L12**

---

## 6. Catalog kontrast vakası — kaynak → trace

`GetProductsHandler.HandleAsync` (`GetProductsHandler.cs:13-27`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :17 | `validator.ValidateAsync` | — *(FluentValidation)* |
| :20 | `Result.Failure<T>` | ✔ |
| :20 | `Error.Validation` | ✔ |
| :22 | `string.Join`, `Errors.Select` | — *(framework)* |
| :25 | `queries.GetProductsAsync` | ✔ `IProductQueries.GetProductsAsync` |
| :26 | `Result.Success` | ✔ |

Endpoint lambda'sı (`ProductEndpoints.cs:24-31`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :29 | `handler.HandleAsync(query, ct)` | ✔ `GetProductsHandler.HandleAsync` |
| :30 | `result.ToHttpResult()` | ✔ `ResultExtensions.ToHttpResult` |

**Atlanan: 0. Fazladan: 0.** 9 düğüm, 9 kenar, derinlik 3.

---

## 7. OrderPaid köprüsü — üç halka da kaynakta teyitli

| # | Halka | Kaynak | Doğrulama |
|---|---|---|---|
| 1 | Raise | [`Order.cs:136`](../../ModularCommerce/src/Modules/Ordering/ModularCommerce.Ordering.Domain/Orders/Order.cs) → `Raise(new OrderPaid(Id, CustomerId, TotalAmount.Amount, TotalAmount.Currency, UpdatedAtUtc))`, `MarkPaid` gövdesinde | ✅ |
| 2 | Mapper | [`OrderingIntegrationEventRegistry.cs:21`](../../ModularCommerce/src/Modules/Ordering/ModularCommerce.Ordering.Infrastructure/Outbox/OrderingIntegrationEventRegistry.cs) → `[typeof(DomainEvents.OrderPaid)] = (OrderPaidType, e => … new ContractEvents.OrderPaid(…))` | ✅ |
| 3 | Consumer | [`OrderPaidNotificationConsumer.cs:7-10`](../../ModularCommerce/src/Modules/Notification/ModularCommerce.Notification.Api/Consumers/OrderPaidNotificationConsumer.cs) → `: IConsumer<OrderPaid>` + `Consume(ConsumeContext<OrderPaid>)` | ✅ |

FlowLens'in ürettiği kenar ve kanıtı:

```
Order.MarkPaid
  => PUBLISHES OrderPaid  (Event, Ordering)  .../Contracts/IntegrationEvents/OrderPaid.cs:2
     evidence: raise .../Order.cs:136 · map .../OrderingIntegrationEventRegistry.cs:21
      => CONSUMES OrderPaidNotificationConsumer.Consume  .../OrderPaidNotificationConsumer.cs:10
```

`Order.MarkPaid` gövdesi (`Order.cs:128-138`) — `TransitionTo` → `Raise` → `Result.Success` —
trace'le birebir örtüşüyor.

**Consumer gövdesi** (`OrderPaidNotificationConsumer.cs:10-32`):

| Kaynak | Çağrı | Trace'te |
|---|---|---|
| :14, :19 | `new NotificationInstruction(...)`, `new NotificationMessage(...)` | — *(constructor → L9, gövdesiz record)* |
| :16 | `nameof(OrderPaidNotificationConsumer)` | — *(operatör, çağrı değil — doğru davranış)* |
| :24 | `processor.ProcessAsync(...)` | ✔ `INotificationProcessor.ProcessAsync` |
| :28 | `new InvalidOperationException(...)` | — *(framework tipi + constructor)* |

**Bir modelleme basitleştirmesi, kayıt için:** kenar `Order.MarkPaid --PUBLISHES--> OrderPaid`
diyor, ama gerçek publish `OutboxDispatcher.cs:100`'de olur. Aradaki halkalar (interceptor →
outbox satırı → BackgroundService) her event için **aynı generic altyapı**, event'e özgü bilgi
taşımıyor. Kenarın `evidence` alanı raise ve mapping noktalarını taşıdığı için iddia
doğrulanabilir kalıyor. Bilinçli seçim, plan §D'de gerekçelendirildi.

---

## 8. Reflection / dynamic / delegate taraması

Checkout zincirindeki tüm dosyalar tarandı: `dynamic`, `Activator.CreateInstance`,
`Type.GetMethod`, `MethodInfo.Invoke`, `GetType()` — **hiçbiri yok.**

Bulunan iki dolaylı dispatch:

| Mekanizma | Yer | FlowLens'in durumu |
|---|---|---|
| Polly `ResiliencePipeline` + lambda | `CardPaymentStrategy.cs:33-34` | Yakalandı (§5.3), mekanizma modellenmedi → **L12** |
| `Func<StockItem, Reservation, Result>` | `StockReservationService.cs:66` | Modül içi yardımcı; zincir dışına çıkmıyor, kayıp yok |

**Reflection kaynaklı kayıp: 0.** Bu ModularCommerce'in özelliği (bilinçli olarak reflection'sız
yazılmış), FlowLens'in başarısı değil — başka bir kod tabanında sonuç farklı olurdu.

---

## 9. "Doğru sebeple mi?" — her interface çözümlemesi

`NotificationProcessor` vakasında sonuç doğruydu ama sebep yanlıştı. Bu tek vaka mı diye **her**
interface çözümlemesi tek tek incelendi. Bu tablo **Faz 5 eval set'inin tasarım girdisi**:
recall/precision sayıları, altındaki mekanizma yanlışsa yanıltıcıdır.

| Interface | Impl | Sonuç doğru mu | Doğru sebeple mi | Gerçek mekanizma |
|---|---:|---|---|---|
| `ICartService` | 1 | ✅ | ✅ | Literal `AddScoped<ICartService, CartService>` (CartModule.cs:34) |
| `ICartCache` | 1 | ✅ | ✅ | Literal singleton |
| `IProductCache` | 1 | ✅ | ✅ | Literal singleton |
| `IStockReservationService` | 1 | ✅ | ✅ | Literal |
| `IPaymentService` | 1 | ✅ | ✅ | Literal |
| `IPspClient` | 1 | ✅ | ✅ | Literal |
| `IOrderRepository` | 1 | ✅ | ✅ | Literal |
| `INotificationProcessor` | 1 | ✅ | ✅ | Literal |
| `ICartRepository` | 2 | ✅ | ⚠️ **Hayır** | **Decorator.** Kayıtlı olan yalnız `CachingCartRepository` (CartModule.cs:30 factory); `PostgresCartRepository`'ye onun **içinden** ulaşılıyor. FlowLens ikisini kardeş implementasyon sanıyor. Tip kümesi doğru, yapı yanlış. |
| `IProductReader` | 2 | ✅ | ⚠️ **Hayır** | Aynı decorator deseni (CatalogModule.cs:59) |
| `IProductQueries` | 2 | ✅ | ⚠️ **Hayır** | Aynı decorator deseni (CatalogModule.cs:52) |
| `IPaymentMethodStrategy` | 1 | ✅ | ⚠️ **Hayır** | **Koleksiyon enjeksiyonu.** `PaymentService` bunu `IEnumerable<IPaymentMethodStrategy>` alıyor (PaymentService.cs:23) ve `FirstOrDefault` ile seçiyor. Tek impl olduğu için şu an fark etmiyor. |
| `INotificationChannel` | 3 | ✅ | ❌ **Hayır** | **Koleksiyon + decorator birlikte.** DI'da **2 kayıt** var (NotificationModule.cs:32,38): `FaultInjecting(Email)` ve `FaultInjecting(Webhook)`. FlowLens 3 **kardeş** implementasyon listeliyor. Üç tip de runtime yolunda olduğu için sonuç doğru — **ama tesadüfen.** |
| `IReservationStrategy` | 3 | ⚠️ **Aşırı-yaklaşım** | ✅ *(bilinçli)* | **Config'e bağlı seçim** (InventoryModule.cs:43-56 switch). Runtime'da 1 aktif; FlowLens 3'ünü de listeliyor. Sound (gerçek yolu kaçırmıyor) ama imprecise — 2'si runtime'da yanlış. Bilinen L3. |

### Özet

| Kategori | Sayı | Anlamı |
|---|---:|---|
| **Doğru sebeple** | 8 | Tek implementasyon; DI yapılandırması ne olursa olsun başka cevap yok |
| **Sonuç doğru, sebep yanlış** (decorator) | 3 | Tip kümesi doğru, sarmalama ilişkisi modellenmiyor |
| **Tesadüfen doğru** (koleksiyon) | 2 | `INotificationChannel`, `IPaymentMethodStrategy` |
| **Bilinçli aşırı-yaklaşım** | 1 | `IReservationStrategy` — statik olarak çözülemez |

**Faz 5 için çıkarım:** 13 çözümlemenin **5'i doğru cevabı yanlış mekanizmayla** veriyor. Bir eval
testi bunları "geçti" sayarsa, kod tabanı değiştiğinde sessizce bozulur. Eval set'i
"sonuç doğru mu" **ve** "hangi mekanizmayla" ikilisini ayrı ayrı ölçmeli.

Somut kırılganlık örneği: `INotificationChannel` kanallarından biri DI'dan kaldırılırsa FlowLens
yine üçünü de listeler — recall düşmez, precision düşer ve test bunu fark etmez.

---

## 10. Verdict

| # | Kriter | Sonuç | Kanıt |
|---|---|---|---|
| 1 | Atlanan in-source çağrı = 0 | ✅ **kaveatlı** | §5.1 (19/19), §5.2, §6. Kaveat aşağıda. |
| 2 | OrderPaid üç halka teyitli | ✅ | §7 — üçü de `dosya:satır` ile |
| 3 | Truncation = 0 | ✅ | §3 — yeni varsayılan 20, ölçülen zincir 10 |
| 4 | Boşluklar sınıflandırılmış | ✅ | §9 + `known-limitations.md` L7/L9–L12 |

### Kriter 1'in kaveatı — açıkça

Roadmap'in `CALLS` tanımı *"A metodu B metodunu çağırıyor"*. **Bu tanıma göre atlanan çağrı 0.**

Ama iki tür in-source referans kenar üretmiyor:
- **Constructor'lar** (`new ChargeRequest(...)` vb.) → L9
- **`static readonly` alan referansları** (`OrderErrors.EmptyCart`) → L10

Doğrulamada kaçırılan constructor'ların **tamamı gövdesiz positional record** çıktı
(`CheckoutResponse`, `ChargeRequest`, `NotificationInstruction`, `PspResult`…) —
**hiçbiri kod yolu gizlemiyor.** Alan referanslarının ait olduğu sınıflar (`OrderErrors`,
`PaymentErrors`) zaten metot çağrılarıyla graph'ta.

Yani Faz 2 kapsamında somut kayıp yok. **Faz 3'te durum değişebilir:** entity construction
(`new Order(...)` deseni) tablo/kolon eşlemesi için önem kazanırsa L9 kapatılmalı.

### Sonuç

**Faz 3'e geçilebilir.** Dört kriter de sağlandı; kaveat dokümante edildi ve Faz 3'e etkisi
değerlendirildi.

**Faz 3'e taşınan iki karar:**
1. **L9 (constructor kenarları)** — entity construction gerekli olursa açılacak.
2. **§9 tablosu** — Faz 5 eval set'i "doğru sebeple mi" boyutunu ölçmeli.

### Bu doğrulamanın kendi sınırı

Elle takip **iki endpoint** için yapıldı (checkout: en uzun zincir; catalog: en kısa + decorator).
Kalan 23 endpoint doğrulanmadı. Roadmap Faz 3 "en az 3 endpoint" istiyor — üçüncüsü Faz 3'te
tablo/kolon eşlemesiyle birlikte yapılacak.
