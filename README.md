# Glass Pour Math Demo (Unity 6)

Bu proje, farklı bardak geometrilerinde hacmi koruyan sıvı katmanlarını ve bardaktan
bardağa dökme animasyonunu içerir.

## Ana bardak referans sahnesi

- `Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity`

Royal Glass Lab, bardakların son görünümü, geometrisi, sıvı profilleri ve dökme
animasyonu için tek canonical referanstır. Royal builder yalnız Royal profil ve
materyallerini kullanır; eski genel profillerden sessizce kopya üretmez.

Sahneyi yeniden üretmek için:

- `Tools > LiquidSort > Rebuild Royal Glass Lab`

Eski `AllGlassesPlayground` sahnesi ve yalnız ona ait görsel zincir proje dışındaki
yerel arşive taşınmıştır. Gerekirse ana projeye değil, ayrı bir deneme projesine
aktarılmalıdır. `Assets/LiquidSort/Profiles` altındaki genel profiller ve bunların
kaynak görselleri gelecekte yeni bardak üretimi için korunur; ikinci bir sahne değildir.

## Oynanabilir raf sahnesi ve taşınabilir rig

- Sahne: `Assets/LiquidSort/SortingShelfShowcase.unity`
- Başka sahneye sürüklenecek prefab: `Assets/LiquidSort/Prefabs/BartenderShelfRig.prefab`
- Yeniden üretim: `Tools > LiquidSort > Rebuild Sorting Shelf Showcase`

Prefab; tam kampanyanın tepe kullanımını karşılayan 34 bardaklık Royal havuzunu,
rafları, level controller/view bağlantılarını,
giriş animasyonunu ve `PourAnimator + PourStream + BartenderPourInteraction` zincirini
tek kökte taşır. Kamera ve `AudioListener` bilinçli olarak prefabın dışında tutulur.
Hedef sahnede `MainCamera` etiketli bir kamera bulunmalı ve bu kamera `Default` layer'ı
görmelidir. Prefab kökünü `(0, 0, 0)` konumunda, `(1, 1, 1)` ölçekte kullanın.

Raf oyununda bir bardağa, ardından hedef bardağa dokunmak dökme işlemini başlatır.
Sipariş arayüzü ayrı bir tüketicidir; portal animasyonu olan teslim butonu/gesture'ı
yalnız `BartenderPourInteraction.TryCommitAndAnimateDelivery` çağırmalıdır. Controller'ın
`TryDeliver` metodu domain/test katmanı içindir; onu doğrudan çağırmak view deferral ve
revision presentation-lock zincirini atlar. Inspector'daki
`Resume Saved Progress` açıksa tamamlanmış bir kampanya kaydı Play modunda boş
`CampaignComplete` görünümü üretebilir; sabit bir test seviyesi için bu seçeneği kapatıp
`Starting Level Number` değerini ayarlayın.

## Korunan çekirdek

- `VesselProfile`: bardak iç poligonu, görünür taban, kapasite ve dökme pozları
- `LiquidBottle`: sıvı bantlarının çizimi ve profil verisi
- `BottleShell`: bardak çerçevesi, gölge ve görsel tema
- `PourAnimator` + `PourStream`: taşıma, eğilme, akış ve geri dönüş animasyonu
- `WaterSortBoard`: yalnız eski/bağımsız sıvı sandbox'ının seçim ve kural katmanı
- `BartenderLevelController` + `BartenderShelfLevelView` + `BartenderPourInteraction`:
  oynanabilir rafın domain, sunum ve input zinciri
- `VesselProfileBaker`: yeni veya güncellenen bardak profilini üretme

`WaterSortBoard` ile Bartender zincirini aynı rig kökünde birlikte kullanmayın.
Prefab kendi controller/session ve otomatik kampanya yüklemesini de taşır. Başka bir
oyunun mevcut FSM/level coordinator'ına entegre ederken prefabı ikinci otorite olarak
çalıştırmak yerine yalnız presentation katmanını bir adapter üzerinden bağlayın.

Aktif profillerin `front`, `traceSource`, materyal veya bake tablolarını eski deneme
asset'leriyle değiştirmeyin. Yeni bir bardak eklerken ayrı bir `VesselProfile` oluşturup
profili baker ile yeniden üretin.

## Unity sürümü

Proje `6000.0.30f1` ile oluşturulmuştur. `Library`, `Temp`, `Logs` ve `UserSettings`
yeniden üretilebilir yerel klasörlerdir ve Git'e dahil edilmez.
