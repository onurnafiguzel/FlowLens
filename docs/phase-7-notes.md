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
