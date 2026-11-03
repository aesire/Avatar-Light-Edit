# Poiyomi Toon 기본 팩의 변형 목록 (ShaderPackBuilder.cs 맨 위 설명 참고). 모든 버전에 같은 목록을 쓴다:
# 셰이더가 선언하지 않은 키워드는 빌더가 버린다.
#
# 크기: 조합 하나가 버전·셰이더마다 0.15MB 안팎이라 자주 보이는 기능만 넣는다.
# 렌더러는 머티리얼 키워드 안에서 가장 많이 맞는 조합을 고르고, 빠진 기능은 "근사 표시"로 알린다.
#
# 1) 기능 하나씩: 매트캡 0·1, 림 0·1, 이미션 0·1, 글리터, 데칼 0, RGB 마스크, 디테일, 색 조정, 디졸브, 뒷면, PBR(Mochie),
#    스타일 스페큘러, 큐브맵, SSS, 클리어코트, 이방성, 노멀 2
.poiyomi/	POI_MATCAP0|COLOR_GRADING_HDR_3D|_GLOSSYREFLECTIONS_OFF|POI_RIM2|_EMISSION|POI_EMISSION_1|_SUNDISK_SIMPLE|GEOM_TYPE_BRANCH|VIGNETTE|FINALPASS|COLOR_GRADING_HDR|DISTORT|POI_BACKFACE|MOCHIE_PBR|POI_STYLIZED_StylizedSpecular|_CUBEMAP|POI_SUBSURFACESCATTERING|POI_CLEARCOAT|POI_ANISOTROPICS|POI_BUMP2NDMAP
# 2) 흔한 조합: 셰이딩(VIGNETTE_MASKED) × 자주 쓰는 조명 방식 × 림·이미션·매트캡 유무
.poiyomi/	VIGNETTE_MASKED _LIGHTINGMODE_TEXTURERAMP|_LIGHTINGMODE_MULTILAYER_MATH|_LIGHTINGMODE_FLAT|_LIGHTINGMODE_REALISTIC ?_GLOSSYREFLECTIONS_OFF ?_EMISSION ?POI_MATCAP0
#    림 스타일 lilToon (자주 쓰임; 기본은 Poiyomi 스타일)
.poiyomi/	VIGNETTE_MASKED _LIGHTINGMODE_TEXTURERAMP|_LIGHTINGMODE_MULTILAYER_MATH|_LIGHTINGMODE_FLAT|_LIGHTINGMODE_REALISTIC _GLOSSYREFLECTIONS_OFF _RIMSTYLE_LILTOON ?_EMISSION ?POI_MATCAP0
#    나머지 조명 방식은 셰이딩만
.poiyomi/	VIGNETTE_MASKED @group:_LIGHTINGMODE_
#    셰이딩 끔 (언릿) × 림·이미션·매트캡 유무
.poiyomi/	?_GLOSSYREFLECTIONS_OFF ?_EMISSION ?POI_MATCAP0
#
# 4) 미리보기에 영향이 없는 기능: 렌더러가 끄고 근사 표시로 치지 않는다
#    (AudioLink·LTCGI·블랙라이트는 월드 장치가 있어야 보이고, BeatSaber 모듈은 9.x 머티리얼에 기본으로 켜져 있다)
ignore	BSSBLOOMFOGTYPE_HEIGHT POIBS_ENABLE POIBS_BLOOMFOG POI_AUDIOLINK POI_AL_DECAL POI_AL_VOLUMECOLOR POI_LTCGI POI_BLACKLIGHTMASKING
#
# 3) 대신 그릴 셰이더 (alias GUID 셰이더이름): 팩에 넣지 않은 변종을 같은 버전의 본 셰이더로 그린다 (근사 표시로 알림).
#    외곽선 먼저 그리기·그랩 패스·월드·릴 퍼 → Toon (Two Pass 계열은 Two Pass, 9.0~9.2 Two Pass는 Toon). 팩에 없는 이름이면 렌더러가 무시한다.
# 10.0
alias	fa7f9dc5d41366249bd0098f22c3c1cd	.poiyomi/Poiyomi Toon
alias	a19e06d869c537d4391955e43f631173	.poiyomi/Poiyomi Toon
alias	aaeaef15081a5154eac3e71b584c5472	.poiyomi/Poiyomi Toon
alias	9ea5d2df175e6f84785d5fffd69809a5	.poiyomi/Poiyomi Toon
alias	b357c1b1407f3ad4eba47df501cc613c	.poiyomi/Poiyomi Toon Two Pass
# 9.3
alias	21e56665a7e45184b9013fb9e4f7bc96	.poiyomi/Old Versions/9.3/Poiyomi Toon
alias	d141019541e4e44479fbd71ad42a2cf6	.poiyomi/Old Versions/9.3/Poiyomi Toon
alias	a6839ad792f95aa41a941c7317aa1fe1	.poiyomi/Old Versions/9.3/Poiyomi Toon
alias	e99d2df55dac44a4c921122fe7cf6ba7	.poiyomi/Old Versions/9.3/Poiyomi Toon
alias	47c3851517c96c84aae06d328a2581f6	.poiyomi/Old Versions/9.3/Poiyomi Toon Two Pass
# 9.2
alias	73da03e2cd0afa940968d9c487e2474d	.poiyomi/Old Versions/9.2/Poiyomi Toon
alias	3210d19aa4851e148b6848177a017f02	.poiyomi/Old Versions/9.2/Poiyomi Toon
alias	00a679886ff24e74585a2a9d37b3c8d7	.poiyomi/Old Versions/9.2/Poiyomi Toon
alias	1b58d372fe360874a8fb767d53f955b0	.poiyomi/Old Versions/9.2/Poiyomi Toon
# 9.1
alias	18de0e547c911de4e804ceaaa97a0b26	.poiyomi/Old Versions/9.1/Poiyomi Toon
alias	7845a1ca48a75804c8925b2bd5680f7f	.poiyomi/Old Versions/9.1/Poiyomi Toon
alias	3eefd743f727e99419fa501e889c4998	.poiyomi/Old Versions/9.1/Poiyomi Toon
alias	7bca35b6e5a09ee4cbdff5cc54a9bd49	.poiyomi/Old Versions/9.1/Poiyomi Toon
# 9.0
alias	fc2c2d7b9ec11d34785997ccbb86f496	.poiyomi/Old Versions/9.0/Poiyomi Toon
alias	aa7e588ee69e46f429e3daa4b3068311	.poiyomi/Old Versions/9.0/Poiyomi Toon
alias	bdbac715113578048ab7114aa3379a1d	.poiyomi/Old Versions/9.0/Poiyomi Toon
alias	68d4032c05798c44eafe02f272e6895c	.poiyomi/Old Versions/9.0/Poiyomi Toon
# 8.1
alias	24edb77705ad65a49afb831f72747592	.poiyomi/Old Versions/8.1/Poiyomi Toon Outline
alias	d0091594032d90d46b8d5eaeaf065a17	.poiyomi/Old Versions/8.1/Poiyomi Toon
alias	c0aca4001823bf74e8a9538b3075bf56	.poiyomi/Old Versions/8.1/Poiyomi Toon
alias	784867d15973a4847b1a6048d42c1565	.poiyomi/Old Versions/8.1/Poiyomi Toon
# 8.0
alias	1513582543e8f6c44b1981e8689f7a52	.poiyomi/Old Versions/8.0/Poiyomi Outline
alias	c933bce24d9d90747a5def016febd042	.poiyomi/Old Versions/8.0/Poiyomi Toon
