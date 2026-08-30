# Glass Pour Math Demo (Unity 6)

Bu proje, verilen `GlassFront.png` kontur/FX asset'iyle videodaki temel sıvı davranışını kurar.
Her bardak üç açık katmandan oluşur:

```text
GlassBase       ← boş iç alan ve yalnız arkada kalan ana yansıma
LiquidSegments ← opak, silindir biçimli renk parçaları
FrameFX        ← ince kontur ve küçük kenar parlaması
```

- Kadeh dönerken sıvı sınırları dünya/ekran ekseninde yatay kalır ve dönen iç şekle kırpılır.
- Sıvı parçalarının gövdesi doğrudan iç poligon dilimlerinden üretilir; `SpriteMask` gerekmez.
- Doluluk yüksekliği sabit bir Y yüzdesiyle değil, poligon alanı korunarak hesaplanır.
- Üst renk katmanı kaynak kadehten azalırken hedef kadehte aynı miktarda büyür.
- Sıvı merkezleri tamamen opaktır; arkadaki cam yansımasını örter.
- Sıvının üzerinde geniş bir cam yüzeyi yoktur. Yalnız ince `FrameFX` ve küçük bir kenar parlaması vardır.

## Açılış

1. Klasörü Unity Hub ile açın (Unity 6 önerilir).
2. İlk derleme bittiğinde `Assets/Scenes/GlassPourDemo.unity` otomatik oluşur.
3. Sahneyi açın ve **Play** düğmesine basın.
4. Fareyle tıklayın veya **Space** tuşuna basın.

Sahne otomatik oluşmazsa: `Tools > Glass Pour Demo > Rebuild Demo Scene`.

Matematik kontrolü: `Tools > Glass Pour Demo > Validate Liquid Math`.

## En önemli ayar noktaları

- İç hazne şekli: `GlassVessel.InteriorPolygon`
- Dönme açısı ve süre: `GlassPourController.tiltAngle`, `pourDuration`
- Başlangıç renkleri/dolulukları: `GlassPourController.Start()`
- Asset hizası: `GlassPourDemoBuilder.ConfigureTexture()` (512 PPU)

Bu örnek dökme mantığını ve katman aktarımını gösterir. Videodaki köpük, sıçrama, ses ve hedef seçme oyun kuralları ayrı VFX/gameplay katmanlarıdır.
