# 닉네임 부분 판독 재현 (Windows)

Windows 한국어 OCR이 설치된 PC에서 원본 이미지와 **최초 OCR의 닉네임 박스**(원본 픽셀 좌표)를 넣는다. 앱과 같은 `ReadNicknamesAsync`를 호출한다. 게임이나 API에 접근하지 않으며 파일을 저장하지 않는다. 개인 캡처는 커밋하지 않는다.

```powershell
dotnet run --project tools/vision/OcrProbe -- <image.png> <x> <y> <width> <height> <initial-name> <expected-name>
```

AM-06 사용자 제공 첫 이미지(451×533)의 박스 `93 420 38 16`, 최초 후보 `염통`, 기대 `염룡`으로 확인했다. 원본 부분을 2배·3배 bicubic 확대해 둘 다 `염룡`(focused=2)을 읽었다. 종료 코드 0은 기대 이름과 일치하고 복수 판독 확인을 통과했다는 뜻이다. 이 한 이미지의 성공을 모든 게임 화면의 정확도로 일반화하지 않는다. 작은 녹색 채팅 캡처에서는 안정적인 판독을 확인하지 못했다.

추가 대비·이진화나 한글 치환표는 사용하지 않는다. 후보가 일치하지 않으면 앱은 자동 검색을 보류한다. 존재하는 계정이라는 사실은 글자의 판독 근거가 아니다. 테스트 `NicknameReadingsTests`는 이 보류 규칙과 잘못 읽힌 이름도 실존하는 경우를 검사한다.

어떤 화면이든 OCR이 읽은 줄과 앱이 고를 후보를 보려면 `--lines`를 쓴다(자르기는 선택).

```powershell
dotnet run --project tools/vision/OcrProbe -- --lines <image.png> [x y width height]
```
