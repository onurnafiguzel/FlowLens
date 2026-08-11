# Faz 7 — Eval set: notlar

LLM yok. Eval, deterministik `AnswerBuilder` üzerinden koşar; soru şeması Faz 8'in `/ask`'ını
şimdiden mümkün kılacak şekilde hem doğal dil sorusunu hem çözülmüş selector'ı taşır.

---

## 1. Oracle'ın 7. adımı — döngüselliği kırmak

Eval'in beklenen kolon kümesini üreten kural (adım 7) ilk hâlinde şuydu:

> INSERT satırın tüm eşlenmiş kolonlarını yazar (`IsRowVersion` hariç); UPDATE yalnız atananları.

**Bu kural FlowLens'in kendi F3/L16 kuralının kopyasıydı** — ve oracle'ı tool'un kuralından
türetmek döngüseldir: implementasyondaki bir hata iki tarafta da bulunur, eval onu **göremez**.

Bağımsız otorite tektir: **EF'in gerçekten ürettiği SQL.** Faz 6'nın harness'ıyla (ModularCommerce'in
derlenmiş DLL'leri referans alındı, hedef repoya tek bayt yazılmadı), gerçek Postgres 17 container'ı
üzerinde EF SQL logging açılarak dört vaka koşuldu.

### Ölçülen SQL

```
A) INSERT — Order.Create + Orders.Add + SaveChanges
   INSERT INTO ordering.orders ("Id","CreatedAtUtc","CustomerId","IdempotencyKey","Status","UpdatedAtUtc")
   INSERT INTO ordering.order_lines ("ProductId","ProductName","Quantity","ReservationId",order_id,"UnitPrice","Currency")
     RETURNING id;
   INSERT INTO ordering.order_status_history ("FromStatus","OccurredAtUtc","ToStatus","TriggeredBy",order_id)
     RETURNING id;                                     (iki kez: Created + StockReserved)

B) UPDATE — reload + MarkPaymentPending + MarkPaid
   INSERT INTO ordering.order_status_history (...) RETURNING id;    (iki kez daha)
   UPDATE ordering.orders SET "Status"=@p10,"UpdatedAtUtc"=@p11 WHERE "Id"=@p12;

C) INSERT — StockItem.Create + Add
   INSERT INTO inventory.stock_items ("Id","CreatedAtUtc","OnHand","ProductId","Reserved","UpdatedAtUtc")
     RETURNING xmin;

D) UPDATE — StockItem.Reserve
   UPDATE inventory.stock_items SET "Reserved"=@p0,"UpdatedAtUtc"=@p1
   WHERE "Id"=@p2 AND xmin=@p3 RETURNING xmin;
```

### Kuralın beş maddesi doğrulandı, biri çürütüldü

| Soru | EF | 7. adım | FlowLens |
|---|---|---|---|
| `IsRowVersion` (`xmin`) INSERT'te? | hayır, `RETURNING` | ✅ | ✅ |
| `xmin` UPDATE `SET`'inde? | hayır, `WHERE`'de | ✅ | ✅ |
| Gölge FK (`order_id`) INSERT'te? | **evet** | ✅ | ✅ |
| UPDATE `SET` yalnız atananlar mı? | evet (2 kolon) | ✅ | ✅ |
| Owned koleksiyon ayrı INSERT mi? | evet, satır başına bir cümle | ✅ | ✅ |
| **Identity PK (`order_lines.id`) INSERT'te?** | **HAYIR**, `RETURNING id` | ❌ | ❌ |

**Düzeltilmiş 7. adım:**

> INSERT satırın tüm eşlenmiş kolonlarını yazar — **değeri veritabanının ürettiği kolonlar hariç**
> (`IsRowVersion` **ve** `IdentityByDefault`). Ayırt edici test: EF onu `RETURNING` ile geri
> okuyorsa yazmıyordur. UPDATE yalnız atananları yazar. DELETE hiçbir kolon yazmaz.

Gerekçe artık *"FlowLens böyle diyor"* değil, ***"EF böyle yazıyor"***.

> **`orders.Id` neden etkilenmiyor:** `ValueGeneratedOnAdd` ama değeri istemci dolduruyor
> (`Shared.Kernel/Entity.cs:7` → `= Guid.NewGuid()`), ve ölçümde INSERT listesinde çıktı.
> Ayırt edici olan `ValueGenerated` bayrağı değil, **store-generated strateji**.

### Bulunan precision kusuru → L21

Düzeltilmiş kural FlowLens'te bir kusur açığa çıkardı: `IdentityByDefault` kolonları `RowInsert`
ile iddia ediliyor. Popülasyon **sekiz DbContext'in tamamı taranarak** ölçüldü:

| | |
|---|---:|
| Toplam identity kolonu (8 snapshot) | **3** |
| Graph'ta iddia edilen | **3 / 3** |
| Yanlış W kenarı | **5** / 109 `RowInsert` |
| Kolon precision'a etkisi | 3 / 97 |

Üçü de owned koleksiyonların sentetik PK'sı: `order_lines.id`, `order_status_history.id`,
`payment_attempts.id`. Ölçülmeyen akışlarda gizli kalan yok — sınıf **tam olarak 3**.

Düzeltme **Faz 7'de yapılmadı**: `graph.json`'ı değiştirir ve bu fazın kapısını kırar. Ayrı iş
olarak sıraya girdi. Eval, kaybı `expectedToFail: L21` ile **öngörülen** olarak raporlar.

### Kayıt: precision nasıl yanlış soruyla %100 çıkar

`phase3-validation.md` §8, satır düzeyi kuralın eklediği 15 kolonun her birini `Migrations/*.cs`'e
karşı tek tek doğruladı ve **"15/15 gerçek. Uydurulmuş kolon yok. Precision %100 korundu."**
yazdı. İki negatif kontrol de vardı (`processed_messages`'ta `Id` yok, `xmin` hiçbir yerde yok) ve
ikisi de tuttu.

Doğrulamanın sorduğu soru: **"bu kolon migration'da var mı?"** → üçü de var, cevap doğru.
Sorulması gereken soru: **"bu akış onu yazıyor mu?"** → üçünü de yazmıyor.

> Aynı veriye bakan iki soru, iki farklı cevap. Precision %100 ölçülmüştü, ama **yanlış soruyla**.
> Faz 6'nın dersinin ("test doğruydu, popülasyon sessizdi") metrik seviyesindeki kardeşi:
> **metrik doğruydu, sorduğu soru yanlıştı.**

Üç ders, üç faz, aynı aile:

| Faz | Yeşil görünen | Gerçekte |
|---|---|---|
| 5 §11.6 | mutasyon testi kırmadı | test **yanlış satırı** koruyordu |
| 6 §7a | mutasyon testi kırmadı | test doğruydu, **popülasyon** sessizdi |
| **7 §1** | precision **%100** | metrik doğruydu, **soru** yanlıştı |

---

## 2. Eval set kendi iç tutarlılığını sınadı ve tutarsız çıktı

İlk koşunun sol-alt kutusunda (öngörülmedi + gerçekleşti) tek soru vardı: **Q19**,
`notification.processed_messages`'ın geri sorusu. Beklenen "1 consumer, 0 endpoint" idi; cevapta
consumer **bulundu**, ama fazladan bir kök geldi: `POST /api/ordering/checkout`.

Fazladan kök gerçekti. Zincir tamamen kaynakta:

```
Order.cs:136                          Raise(new OrderPaid(...))
OrderingIntegrationEventRegistry.cs:21-25   domain -> integration eşlemesi
OrderPaidNotificationConsumer.cs:8,24       IConsumer<OrderPaid> -> processor.ProcessAsync
NotificationProcessor.cs:53                 ProcessedMessages.Add(...)
```

**Asıl bulgu bu değil.** Asıl bulgu, aynı köprü hakkında **kendi soru setimin iki farklı şey
iddia etmesi**: Q01 checkout'un ileri cevabında `notification.processed_messages`'ı bekliyor —
ve o iddia **doğrulandı**. Aynı köprü ters yönde geçilmezse Q01 ile Q19 aynı anda doğru olamaz.

> Kaçırma tool'da değil **oracle'daydı**, ve onu bulan şey FlowLens'in çıktısı değil, eval set'in
> **kendi içindeki çelişki** oldu. Faz 1'in *"68 proje / doğrusu 66"* dersinin bu fazdaki
> karşılığı — o zaman hatayı fark eden bir insan olmuştu, bu kez ölçüm aracının kendisi.

### Çapraz kontrol elle değil makineyle yapıldı

Q19'u fark ettikten sonra sorunun tek olup olmadığı **taranarak** cevaplandı: her
(ileri soru → tablo T) çifti, T'nin geri sorusunun kök listesine karşı kontrol edildi.

| Tablo | Geri | İleri eşleri | Sonuç |
|---|---|---|---|
| `cart.carts` | Q18 | Q01, Q06 | ✓ |
| `catalog.outbox_messages` | Q21 | Q03, Q13 | ✓ |
| `discovery.product_embeddings` | Q16 | Q03, Q10, Q12, Q13 | ✓ |
| `inventory.stock_items` | Q17 | Q01, Q02, Q04, Q14 | ✓ |
| `ordering.order_lines` | Q20 | Q01, Q02 | ✓ |
| `notification.processed_messages` | Q19 | Q01, Q09 | **✗** |

Çelişki tekti. **Ama kapsam değil:** 16 tablonun yalnız **6'sının** iki yönü de soruluyor. Kalan
on tablo (`catalog.products`, `identity.users`, `identity.refresh_tokens`, `inventory.reservations`,
`notification.notification_logs`, `ordering.orders`, `ordering.order_status_history`,
`ordering.outbox_messages`, `payment.payments`, `payment.payment_attempts`) çapraz kontrol
**edilemiyor** — tutarlı oldukları için değil, **sınanmadıkları** için sessizler. Bu, eval set'in
ölçülmüş bir sınırı olarak kaydedildi.

### Popülasyon iddiası da çürüdü

Q19 *"kök kümesi YALNIZ Consumer olan tek tablo"* diyordu, `count: 1`. Ölçüm: graph genelinde
yalnız-consumer tablo **sıfır**. Consumer kökü *bulunan* tablo **3** — ve üçünün de kök kümesi
karışık.

| Tablo | Kök kümesi |
|---|---|
| `discovery.product_embeddings` | 2 endpoint + 1 consumer + 1 arka plan işi |
| `notification.notification_logs` | 2 endpoint + 1 consumer |
| `notification.processed_messages` | 1 endpoint + 1 consumer |

> Popülasyon sayımı yalnız **tanıma** değil, **beklenen değerin kendisine** de duyarlıymış: yanlış
> bir `expected`, yanlış bir popülasyon tanımı üretiyor. İkisi aynı hatanın iki yüzü.

---

## 3. Kapıyı düzeltmeden önce yazmak — bir yerine iki örnek

İlk koşuda Q06 sağ-üst kutuya düştü: *"öngörüldü, gerçekleşmedi"*, yani rapor **öngörünün
yanlış olduğunu** söylüyordu. Değildi. Q06 `F2`/`L17` öngörüyordu ama `externalStores`'u hiç
**iddia etmiyordu** — cevapta oynayabilecek hiçbir eksen yoktu. Öngörü yanlış değil,
**ölçülemezdi**.

İkisi farklı sonuç ve 3×2 onları aynı kutuda gösteriyordu. Kapı yazıldı:

```
EveryPredictedFailureHasAnAxisThatCouldRealiseIt
  her expectedToFail girdisi icin, o sinirin etkileyebilecegi eksen
  (tables / roots / events / externalStores / limitations / nodes)
  expected'da var mi?
```

**Kapı düzeltmeden ÖNCE yazıldı ve 22 sorunun tamamını taradı.** Sonuç: 2 soru, 4 girdi —
Q06 **ve Q01**. Q01 aynı kusuru taşıyordu ve kimse bakmıyordu.

> Kapıyı Q06 düzeltildikten sonra yazsaydım, Q06'ya göre şekillenir ve Q01 sessiz kalırdı.
> Faz 6'nın kuralının ("eksik testi fixture'dan değil graph'tan seç") soru seti üzerindeki
> karşılığı: **eksik kapıyı bilinen vakadan değil, popülasyonun tamamından türet.**

Düzeltmeden sonra Q06 sağ-üstten sol-üste geçti — ölçülebilir hale gelince öngörü **tuttu**.

### Üç oracle düzeltmesi, üç ayrı commit

| Soru | Ne değişti | Kaynak kanıtı |
|---|---|---|
| Q19 | `roots.endpoint` += checkout; popülasyon yeniden ölçüldü | `Order.cs:136` · `OrderPaidNotificationConsumer.cs:8` |
| Q06 | `externalStores: ["RedisCartCache"]` | `CachingCartRepository.cs:34` |
| Q01 | `externalStores: ["RedisCartCache"]` | `CartService.cs:13,27` → `RedisCartCache.cs:43` |

Üçü de runner commit'inden **ayrı**, her biri düzeltmeyi çürüten kaynak satırını mesajında
taşıyor. Böylece *"beklenen değer çıktıya uydurulmuş mu?"* sorusu tek bir `git log` ile
cevaplanıyor.

### Rapor kusuru da bir bulgu

`tablo (erisim R/W)` satırı popülasyon olarak **uyuşmazlıkları** kullanıyordu: 37 kontrolün 6
uyuşmazlığı `6 beklenen · 0 bulunan · %0` diye okunuyordu — ölçülenden çok daha kötü bir sayı ve
uyuşan 31 hakkında hiçbir şey söylemiyor. Popülasyon **bulunan tablolar** olarak düzeltildi:
`37 · 31 · %83,8`. Sessizce düzeltilmedi, ayrı commit'e kondu.

### Tek satırda "7 kaçırıldı" iki farklı sebebi gizliyor

Q01 ve Q02, `ordering.outbox_messages`'ın **yedi kolonunun yedisini de** kaçırıyor. Tek satır
tek bir sebep varmış gibi okunuyor; ölçüm iki tane gösterdi.

| Kolon | Graph'ta node var mı | Neden kaçırıldı |
|---|---|---|
| `Id`, `Type`, `Content`, `OccurredOnUtc` | **hayır** | Interceptor gövdesi hiçbir akışın erişilebilir kümesinde değil — **L16-4** |
| `Error`, `ProcessedOnUtc`, `RetryCount` | **evet** | Node var ama checkout'un alt grafiğinden **erişilemiyor**; `OutboxDispatcher`'dan geliyorlar |

İkinci satırın kanıtı aynı koşuda: **Q15 üçünü de buluyor.** Yani "outbox kolonları görünmüyor"
cümlesi yanlış — üçü görünüyor, sadece başka bir kökten. L16-4'ün gerçek kapsamı yedi değil
**dört** kolon.

> Kaçırma sayısı bir mekanizma sayısı değil. Aynı hücrede iki farklı sebep toplanabiliyor ve
> rapor bunu ayırt etmiyor; ayırt eden şey soru soru okumak oldu.

### `raw-sql` uyarısı geri sorularda yapısal olarak çıkmıyor

Q16 (`discovery.product_embeddings` geri sorusu) beklenen `raw-sql` sınır kodunu **almadı**.
Sebep F6/L6'nın kapsamında ama mekanizması ayrı ve kayda değer.

`Limitations`, build diagnostics'ini **alt graftaki dosyalarla eşleştirerek** üretiyor
(`AnswerBuilder.cs:405,415`): bir diagnostic'in `file:line`'ı, cevabın ulaştığı düğümlerden
birinin dosyasıyla örtüşüyorsa o cevabın sınırı sayılıyor.

- **İleri yönde çalışıyor:** `POST /api/discovery/search` akışı `ProductVectorRepository.cs`'e
  uğrar, dosya eşleşir, uyarı çıkar.
- **Geri yönde çıkamıyor:** ham SQL zaten kenar üretmediği için geri yürüyüş o dosyaya **hiç
  uğramaz**, dolayısıyla eşleşecek dosya yoktur.

Sonuç: *"bu tabloya bakamadığım bir yer var"* uyarısı **tam da en çok gerekli olduğu yerde** —
"bu tabloya kim dokunuyor?" sorusunda — yapısal olarak üretilemiyor. Faz 3'ün *"graph
'dokunmuyor' demez, 'bakamadım' der"* kuralı geri yönde tutmuyor.

### Sol-alt kutu artık boş — ve bu bir başarı değil

Düzeltmelerden sonra öngörülmeyen kaçırma **kalmadı**. Yani bu koşuda eval, FlowLens hakkında
sürpriz bir şey bulmadı; **kendi hakkında üç şey** buldu. Sol-alt kutunun boş olması, soruların
yalnız öngörülen şeyleri bulduğu anlamına da gelebilir — bir sonraki koşuda şüphelenilecek yer
burasıdır.
