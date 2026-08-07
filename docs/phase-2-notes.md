# Faz 2 — Tek endpoint'in call chain'i (tamamlandı)

> Ölçüm tarihi: 2026-08-07 · Hedef: `ModularCommerce.sln` (66 proje, 48'i test dışı) · SDK 10.0.301

---

## 1. Kabul kriterleri

| Roadmap kriteri | Durum | Kanıt |
|---|---|---|
| Endpoint → Handler → Service → Repository zinciri basılıyor | ✅ | `flowlens trace` çıktısı, §3 |
| Publish edilen event ve onu consume eden handler zincire dahil | ✅ | `Order.MarkPaid --PUBLISHES--> OrderPaid --CONSUMES--> OrderPaidNotificationConsumer` |
| Sonsuz döngüye girmiyor | ✅ | `CallGraphWalkerTests`, karşılıklı özyineleme testi |
| **L1 kapandı** | ✅ | 25 endpoint, **0 unresolved route** |
| En az bir integration test | ✅ | 57 test, tamamı yeşil, 0 atlanan |

---

## 2. Ölçülen sayılar

```
Loaded 66/66 projects in 16,1s
Endpoint discovery (48 non-test projects): 4,97s
  25 endpoints · 0 unresolved route · 0 candidates eliminated · 0 multi-mount
  pass 1: 25 map calls, 11 prefix propagations · pass 2: 19 methods reached
Messaging model: 4 domain→integration mappings, 3 consumer registrations (0,1s)
Checkout trace: 106 nodes · 185 edges · max depth 10 · 0,5s
  (Endpoint 1, Handler 4, Method 89, Repository 11, Event 1)
  369 invocations examined · 369 resolved by symbol · 0 unresolved · 154 framework-filtered
  28 interface calls · 12 ambiguous nodes · 0 truncated
  SymbolFinder: 23 calls, 6 cache hits
```

### Performans — 3 koşu ortalaması

| Aşama | Hedef (plan) | Ortalama | Aralık | Durum |
|---|---|---:|---|---|
| Solution yükleme | ≤ 20 s | **16,1 s** | 15,8–16,3 | ✅ |
| Endpoint keşfi | ≤ 5 s | **4,97 s** | 4,9–5,0 | ✅ *sınırda* |
| Zincir + SymbolFinder | ≤ 30 s | **0,5 s** | — | ✅ |
| **`flowlens trace` toplam** | **≤ 60 s** | **23 s** | 22–24 | ✅ |

Zincir yürüyüşü tahminimden ~40× hızlı çıktı. Sebep: `SymbolFinder` yalnız **interface**
çağrılarında devreye giriyor (23 çağrı), concrete çağrılar hiç dokunmuyor.

> **İlk raporda endpoint keşfi için "5,5 s ✘" yazmıştım — o tek koşuydu ve yanıltıcıydı.**
> Build'den hemen sonra, soğuk dosya cache'iyle alınmış. Üç koşunun hiçbiri 5,0 s'yi aşmıyor.
> Ama hedef **sınırda** tutuyor (%0,6 pay); ModularCommerce birkaç modül büyürse aşılır. O noktada
> ya `MentionsAnyMapVerb`'in `GetText()` ön filtresi ucuzlatılmalı ya da hedef gerekçesiyle
> revize edilmeli. Detay: [phase2-validation.md §4](phase2-validation.md).

### Traversal derinliği — ölçüldü, tahmin edilmedi

| `--max-depth` | Düğüm | Truncated |
|---:|---:|---:|
| 10 (eski varsayılan) | 106 | **1** |
| 11 | 106 | 0 |
| 50 | 106 | 0 |

**ModularCommerce'te en uzun zincir 10 seviye** (`POST /api/ordering/checkout`); catalog 3.
Truncation raporlamayan ilk sınır 11. **Varsayılan 20 seçildi** — ölçülen değerin ~2 katı, böylece
biraz daha derin bir akış da varsayılanla tamamlanır.

Eski varsayılan 10 **hiçbir düğüm kaybetmemişti**: derinlik 10 ile derinlik 50 birebir aynı graph'ı
üretiyor. Sınırdaki düğüm, genişletilmesinin bir şey ekleyip eklemeyeceği bilinmeden işaretleniyor
— burada eklemiyordu (`FakePspClient.Roll`'un tek çağrısı `Random.Shared.NextDouble()`, framework).
Bayrak muhafazakârdı, yanlış değildi; ama "belki eksik" bir çıktı varsayılan olmamalı.

**Node bütçesi:** sınırsız derinlikte 106 düğüm, bütçe 5000 — aşılma yok.

Derinlik dağılımı seviye 5'te tepe yapıp sönüyor (1·3·19·16·14·**20**·14·10·7·1·1).
Faz 3'ün `Forward()` BFS'i birkaç yüz düğümden fazlasını gezmeyecek.

### Endpoint envanteri

25 endpoint = survey §2.2'deki **24 modül endpoint'i** + `GET /` (`Program.cs:79`).
Route'ların tamamı survey ile birebir eşleşiyor. `/health/live` ve `/health/ready` **yok** —
`MapHealthChecks` kullanıyorlar, bir Map fiili değil (→ `known-limitations.md` **L7**).

---

## 3. Checkout zinciri — köprünün tam hali

```
endpoint:POST /api/ordering/checkout        OrderEndpoints.cs:22
  -> CheckoutHandler.HandleAsync            CheckoutHandler.cs:23
      -> ICartService.GetItemsAsync         → CartService → ICartRepository
                                              → CachingCartRepository  [ambiguous]
                                              → PostgresCartRepository [ambiguous]
      -> IProductReader.GetByIdsAsync       → CachingProductReader [ambiguous]
                                              → ProductReader        [ambiguous]
      -> IStockReservationService.ReserveAsync → IReservationStrategy
                                              → Naive / OptimisticConcurrency / RedisLock [ambiguous]
      -> IPaymentService.ChargeAsync        → PaymentService
      -> Order.MarkPaid                     Order.cs:128
          => PUBLISHES OrderPaid            Contracts/IntegrationEvents/OrderPaid.cs:2
             evidence: raise Order.cs:136 · map OrderingIntegrationEventRegistry.cs:21
              => CONSUMES OrderPaidNotificationConsumer.Consume
                  -> INotificationProcessor.ProcessAsync → NotificationProcessor
                      -> INotificationChannel.SendAsync
                          → Email / Webhook / FaultInjecting [ambiguous]
```

**Domain event node değil, kenarın kanıtı.** Ontoloji büyümedi (roadmap §5), ama
`raiseSite` + `mappingSite` sayesinde iddia doğrulanabilir.

**Internal domain event'ler** (raise edilmiş, registry'de karşılığı yok — köprü hatası **değil**,
outbox interceptor bunları bilerek atlıyor):
`OrderCreated`, `OrderStatusChanged`, `StockReserved`, `ProductSoldOut`, `StockReleased`,
`StockCommitted`, `PaymentCompleted`, `PaymentFailed` — 8 adet, hepsi doğru sınıflandı.

---

## 4. Üç bug, üç ders

Faz 2'nin asıl kazancı bunlar. Üçü de sessizce yanlış cevap üretiyordu.

### 4.1 Extension method: reduced vs unreduced form

**Belirti:** İlk çalıştırmada 25 endpoint'in 24'ü `unresolved route`. Sadece `GET /` çalıştı.

**Sebep:** `group.MapOrderEndpoints()` çağrı yerinde Roslyn **reduced** forma bağlanıyor —
`this` parametresi düşmüş, imza `MapOrderEndpoints()`. Metodun gövdesindeki
`GetEnclosingSymbol` ise **unreduced** formu döndürüyor: `MapOrderEndpoints(IEndpointRouteBuilder)`.
İki farklı sembol, iki farklı sözlük anahtarı → prefix yayılımı hedefini hiç bulamıyor.

**Çözüm:** `NodeId.Canonical(symbol) = (symbol.ReducedFrom ?? symbol).OriginalDefinition`.
Sembolün kimlik olarak kullanıldığı **her** yerde uygulanıyor.

### 4.2 Sembol kimliği compilation başına, solution başına DEĞİL

**Belirti:** Prefix'ler düzeldikten sonra zincir çalıştı ama `PUBLISHES` kenarı hiç oluşmadı;
`OrderPaid` "internal domain event" olarak sınıflandı.

**Sebep:** `DomainEventBridge` registry'yi `Ordering.Infrastructure` compilation'ından okuyor,
raise sitesini ise `Ordering.Domain` compilation'ından. `Ordering.Domain.Orders.OrderPaid` bu iki
compilation'da **farklı `ITypeSymbol` instance'ları** ve `SymbolEqualityComparer.Default` bunları
eşit görmüyor. Aynı sorun `ConsumerIndex`'te de vardı (publisher ile consumer farklı
compilation'lar).

**Bu, planımdaki bir iddianın yanlış olduğunu gösterdi.** Plan §5'te "tek `Solution` canlı
tutulacağı için sembol kimliği stabil" yazmıştım. Doğrusu: **kimlik bir compilation içinde
stabildir.** Projeler arası referanslarda değil.

**Çözüm:** Projeler arası eşleme yapan tüm sözlükler tam nitelikli **isimle** anahtarlanıyor
(`NodeId.ForType` / `NodeId.ForMethod`). `ImplementationResolver` cache'i de öyle — sembolle
anahtarlansaydı doğru çalışırdı ama neredeyse her çağrıda ıskalardı.

### 4.3 Map çağrısının kendi relative prefix'i düşüyordu

**Belirti:** `RoutePrefixResolverTests` yakaladı — `endpoints.MapGroup("/api").MapPost(Route, …)`
`/checkout` üretiyordu, `/api/checkout` yerine.

**Sebep:** Pass 2'nin Parameter dalı `Combine(incomingPrefix, routeSuffix)` yapıyordu; çağrının
kendi `Origin.RelativePrefix`'ini atlıyordu. Prefix bir yerel değişkenden geliyorsa (ModularCommerce
deseni) sorun görünmüyor — inline zincirde görünüyor.

**Ders:** Gerçek repoya karşı yeşil olmak yetmiyor. Sentetik test tam da gerçek repoda bulunmayan
şekli denediği için yakaladı.

---

## 5. Node ID formatı — SABİT

Faz 3 bunu `graph.json`'ın `id` alanına yazacak. **Değiştirmek her kayıtlı graph'ı ve testi kırar.**
Tek otorite: [`NodeId.cs`](../src/FlowLens.Core/NodeId.cs).

**Metotlar** — `NodeId.Canonical(symbol).ToDisplayString(MemberFormat)`:

```
globalNamespaceStyle  : Omitted
typeQualificationStyle: NameAndContainingTypesAndNamespaces
genericsOptions       : IncludeTypeParameters
memberOptions         : IncludeContainingType | IncludeParameters
parameterOptions      : IncludeType
misc                  : UseSpecialTypes | EscapeKeywordIdentifiers
```

```
ModularCommerce.Ordering.Application.Orders.Checkout.CheckoutHandler.HandleAsync(ModularCommerce.Ordering.Application.Orders.Checkout.CheckoutCommand, System.Threading.CancellationToken)
ModularCommerce.Discovery.Api.Consumers.ProductChangedConsumer.Consume(MassTransit.ConsumeContext<ModularCommerce.Catalog.Contracts.IntegrationEvents.ProductCreated>)
```

**Parametreler zorunlu:** `ProductChangedConsumer` iki `Consume` overload'u taşıyor, yalnızca
`ConsumeContext<T>` argümanıyla ayrılıyorlar. Parametresiz ID onları tek node'a çökertir ve iki
ayrı `CONSUMES` kenarı birleşir. `NodeIdTests` bunu sabitliyor.

**Metot olmayan node'lar** — önekli, çakışma imkânsız:

```
endpoint:POST /api/ordering/checkout
event:ModularCommerce.Ordering.Contracts.IntegrationEvents.OrderPaid
```

`event:` önekinde tam nitelikli isim **şart**: `Domain.Orders.OrderPaid` ile
`Contracts.IntegrationEvents.OrderPaid` aynı kısa ada sahip, farklı tipler.

### ⚠️ Kabul edilen kaviat

ID parametre tiplerini içerdiği için **imza değişirse ID değişir.** Bu doğru davranış (farklı
metot) ama commit'ler arası graph diff'inde gürültü yaratır: bir parametre eklemek o metodu
"silinmiş + eklenmiş" gösterir. Faz sonrası "incremental update" işine geçilirse bu hesaba
katılmalı.

---

## 6. AMBIGUOUS politikası — gözlenen etki

Plan §B'deki karar `AllImplementations`. Gerekçe ölçümdü: (ii) `DeclaringModuleOnly` hiçbir şey
filtrelemiyor çünkü modül sınırı kuralı implementasyonları zaten kendi modülünde tutuyor;
(iii) DI kayıtlarını okumak da işe yaramıyor çünkü belirsiz interface'lerin hiçbiri literal
`AddScoped<IX, X>()` formunda değil.

**Faz 2'de gözlenen:** checkout trace'inde 5 ayrı interface belirsiz çıktı. Hepsinde "tüm
implementasyonlar" doğru cevaptı:

| Interface | Impl | Neden hepsi doğru |
|---|---|---|
| `ICartRepository` | 2 | Decorator + concrete, **ikisi de** runtime yolunda |
| `IProductQueries` | 2 | Aynı |
| `IProductReader` | 2 | Aynı |
| `IReservationStrategy` | 3 | Config seçiyor — statik olarak bilinemez |
| `INotificationChannel` | 3 | Email + Webhook **ikisi de** çalışıyor, FaultInjecting sarmalıyor |

Politika `ImplementationPolicy` enum'u ile takılıp çıkarılabilir:
`--implementation-policy all|declaring-module`.

---

## 7. Testler

**57 test, 25 saniye, 0 atlanan.** Faz 1 kuralları korundu:

- **Sessiz geçme yok** — hedef solution yoksa `Phase2Fixture` kurulum talimatlarıyla fail eder.
  Bozuk yolla doğrulandı.
- **Sabit sayı yok** — `24` veya `9` gibi literaller yerine çapa route'lar
  (`POST /api/ordering/checkout`), invariant'lar (`0 unresolved route`) ve runtime karşılaştırmaları.
  Günün sayıları burada, testte değil.

Gruplar: `RoutePrefixResolverTests` (7, sentetik) · `CallGraphWalkerTests` (4, sentetik) ·
`NodeIdTests` (6, sentetik) · `Phase2IntegrationTests` (11, gerçek repo) · Faz 1'den devralınan
`ProjectClassifierTests` / `CompilationCheckerTests` / `SolutionLoaderIntegrationTests`.

`SyntheticWorkspace` `AdhocWorkspace` üzerine kurulu — MSBuild yok, milisaniyeler. ASP.NET Core
sembollerine bağlanabilmesi için test projesi `FrameworkReference Microsoft.AspNetCore.App`
alıyor ve referanslar test host'unun TPA listesinden geliyor.

---

## 8. CLI

```
flowlens endpoints <sln>                              # keşif + eleme/unresolved raporu
flowlens trace <sln> --endpoint "POST /api/..." \     # tek endpoint'in zinciri
        [--max-depth N] [--implementation-policy all|declaring-module]
```

Exit code'lar: `0` temiz · `1` yükleme problemi · `2` derleme hatası · `3` eksik analiz
(unresolved route / bütçe tükendi) · `4` endpoint bulunamadı · `64` kullanım hatası.

Faz 2 komutları yükleme sorunu varsa **hiç çalışmıyor** — sessizce atlanan bir proje,
"bu metot çağrılmıyor" ile "hiç bakmadık"ı ayırt edilemez yapar.

---

## 9. Kapsam dışı (Faz 3)

`graph.json`, EF Core `IModel`, `Forward`/`Backward` API'si, `READS`/`WRITES`/`MAPS_TO` kenarları.
Faz 2 yalnız `CALLS` / `PUBLISHES` / `CONSUMES` üretiyor ve konsola basıyor.

`TraceNode`/`TraceEdge` alan adları Faz 3'ün `Node`/`Edge` şemasına yakın tutuldu — dönüşüm ucuz
olmalı.
