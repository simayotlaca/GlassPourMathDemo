# Glass Pour Math Demo (Unity 6)

Bu proje, farklı bardak geometrilerinde hacmi koruyan sıvı katmanlarını ve bardaktan
bardağa dökme animasyonunu içerir.

## Ana sahneler

- `Assets/LiquidSort/RoyalGlassLab/RoyalGlassLab.unity`
- `Assets/LiquidSort/AllGlassesPlayground.unity`

Bu iki sahne aynı sıvı çizim ve animasyon çekirdeğini kullanır. Royal sahnesi ayrı
görsel/profile setine sahiptir; `MugRoyal.asset` iki sahne arasında ortaktır.

Sahneleri yeniden üretmek için:

- `Tools > LiquidSort > Rebuild Royal Glass Lab`
- `Tools > LiquidSort > Rebuild All Glasses Playground`

## Korunan çekirdek

- `VesselProfile`: bardak iç poligonu, görünür taban, kapasite ve dökme pozları
- `LiquidBottle`: sıvı bantlarının çizimi ve profil verisi
- `BottleShell`: bardak çerçevesi, gölge ve görsel tema
- `PourAnimator` + `PourStream`: taşıma, eğilme, akış ve geri dönüş animasyonu
- `WaterSortBoard`: seçim ve geçerli sıvı aktarımı
- `VesselProfileBaker`: yeni veya güncellenen bardak profilini üretme

Aktif profillerin `front`, `traceSource`, materyal veya bake tablolarını eski deneme
asset'leriyle değiştirmeyin. Yeni bir bardak eklerken ayrı bir `VesselProfile` oluşturup
profili baker ile yeniden üretin.

## Unity sürümü

Proje `6000.0.30f1` ile oluşturulmuştur. `Library`, `Temp`, `Logs` ve `UserSettings`
yeniden üretilebilir yerel klasörlerdir ve Git'e dahil edilmez.
