cmake_minimum_required(VERSION 3.30)

cmake_policy(SET CMP0091 NEW)
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
project(BmsVorbisBridge VERSION 1.0.0 LANGUAGES C CXX)

include(FetchContent)

set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build static Xiph dependencies" FORCE)
set(BUILD_TESTING OFF CACHE BOOL "Disable upstream test targets" FORCE)
set(BUILD_EXAMPLES OFF CACHE BOOL "Disable upstream example targets" FORCE)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>" CACHE STRING "Use the static MSVC runtime" FORCE)

if(MSVC)
  add_compile_options(/fp:precise)
endif()

FetchContent_Declare(
  ogg
  URL https://downloads.xiph.org/releases/ogg/libogg-1.3.6.tar.xz
  URL_HASH SHA256=5C8253428E181840CD20D41F3CA16557A9CC04BAD4A3D04CCE84808677FA1061
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
)
FetchContent_MakeAvailable(ogg)

FetchContent_Declare(
  vorbis
  URL https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz
  URL_HASH SHA256=B33CC4934322BCBF6EFCBACF49E3CA01AADBEA4114EC9589D1B1E9D20F72954B
  DOWNLOAD_EXTRACT_TIMESTAMP TRUE
)
set(OGG_INCLUDE_DIR "${ogg_SOURCE_DIR}/include" CACHE PATH "Pinned libogg headers" FORCE)
set(OGG_LIBRARY "ogg" CACHE STRING "Pinned static libogg target" FORCE)
set(OGG_VERSION_STRING "1.3.6" CACHE STRING "Pinned libogg version" FORCE)
FetchContent_MakeAvailable(vorbis)

add_library(bms_vorbis SHARED src/bms_vorbis.cpp)
target_compile_features(bms_vorbis PRIVATE cxx_std_17)
if(MSVC)
  target_compile_options(bms_vorbis PRIVATE /utf-8)
endif()
target_include_directories(bms_vorbis PRIVATE ${ogg_SOURCE_DIR}/include ${vorbis_SOURCE_DIR}/include)
target_link_libraries(bms_vorbis PRIVATE vorbisfile vorbis ogg)
target_compile_definitions(bms_vorbis PRIVATE BMS_VORBIS_EXPORTS)
set_target_properties(bms_vorbis PROPERTIES
  OUTPUT_NAME bms_vorbis
  CXX_VISIBILITY_PRESET hidden
  VISIBILITY_INLINES_HIDDEN ON
)

add_executable(vorbis_reference tools/vorbis_reference.cpp)
target_compile_features(vorbis_reference PRIVATE cxx_std_17)
target_include_directories(vorbis_reference PRIVATE ${ogg_SOURCE_DIR}/include ${vorbis_SOURCE_DIR}/include)
target_link_libraries(vorbis_reference PRIVATE vorbisfile vorbis ogg)
