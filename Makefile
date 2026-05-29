TFM      := netstandard2.1
CONFIG   := Debug
DLL      := VGTTS.dll

BUILDDIR := VGTTS/bin/$(CONFIG)/$(TFM)
BUILDDLL := $(BUILDDIR)/$(DLL)

# WSL path to the game install — adjust if Steam lives elsewhere
GAME_DIR := /mnt/c/Program Files (x86)/Steam/steamapps/common/Vanguard Galaxy
PLUGIN_DIR := $(GAME_DIR)/BepInEx/plugins

# Resolve dotnet — prefer explicit local SDK, fall back to PATH
DOTNET   ?= $(shell command -v dotnet 2>/dev/null || echo /tmp/dnsdk/dotnet/dotnet)

BEPINEX_VERSION := 5.4.23.5
BEPINEX_URL     := https://github.com/BepInEx/BepInEx/releases/download/v$(BEPINEX_VERSION)/BepInEx_win_x64_$(BEPINEX_VERSION).zip

.PHONY: all build link-asm clean deploy deploy-bundle deploy-prerender \
        install-bepinex check-bepinex \
        download-kokoro package

all: build

# One-time: download and unpack BepInEx 5 into the game folder.
# Safe to re-run: overwrites loader files but leaves user plugins + config alone.
install-bepinex:
	@if [ -d "$(GAME_DIR)/BepInEx" ]; then \
		echo "BepInEx already installed at $(GAME_DIR)/BepInEx" ; \
	else \
		mkdir -p /tmp/bepinex-dl ; \
		curl -L -o /tmp/bepinex-dl/bepinex.zip "$(BEPINEX_URL)" ; \
		cd "$(GAME_DIR)" && unzip -o /tmp/bepinex-dl/bepinex.zip ; \
		echo "BepInEx installed. Launch the game once to initialize plugin folders." ; \
	fi

check-bepinex:
	@test -d "$(GAME_DIR)/BepInEx/plugins" || { \
		echo "BepInEx plugins dir not found at $(GAME_DIR)/BepInEx/plugins." ; \
		echo "Run 'make install-bepinex' then launch the game once." ; \
		exit 1 ; \
	}

# Symlink the game's Assembly-CSharp.dll into VGTTS/lib/ for compilation references.
link-asm:
	@mkdir -p VGTTS/lib
	@if [ ! -e "VGTTS/lib/Assembly-CSharp.dll" ]; then \
		ln -sf "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Assembly-CSharp.dll" VGTTS/lib/Assembly-CSharp.dll ; \
		echo "Linked Assembly-CSharp.dll" ; \
	fi
	@if [ ! -e "VGTTS/lib/Newtonsoft.Json.dll" ]; then \
		ln -sf "$(GAME_DIR)/VanguardGalaxy_Data/Managed/Newtonsoft.Json.dll" VGTTS/lib/Newtonsoft.Json.dll ; \
		echo "Linked Newtonsoft.Json.dll" ; \
	fi

build: link-asm
	DOTNET_ROOT=$(dir $(DOTNET)) $(DOTNET) build VGTTS/VGTTS.csproj -c $(CONFIG)

# Install layout: everything under $(PLUGIN_DIR)/VGTTS/ — the plugin resolves
# prerender/ and tools/ relative to its own DLL location (no extra VGTTS/
# nesting). One folder, self-contained.
VGTTS_DIR := $(PLUGIN_DIR)/VGTTS

deploy: build check-bepinex
	@mkdir -p "$(VGTTS_DIR)"
	cp "$(BUILDDLL)" "$(VGTTS_DIR)/"
	@if [ -f "$(BUILDDIR)/VGTTS.pdb" ]; then cp "$(BUILDDIR)/VGTTS.pdb" "$(VGTTS_DIR)/"; fi
	# Game 0.8.1 dropped Newtonsoft.Json.dll from Managed/; ship our own copy
	# (CopyLocalLockFileAssemblies puts it in bin). Skips gracefully if absent.
	@if [ -f "$(BUILDDIR)/Newtonsoft.Json.dll" ]; then cp "$(BUILDDIR)/Newtonsoft.Json.dll" "$(VGTTS_DIR)/"; fi
	@echo "Deployed $(DLL) to $(VGTTS_DIR)"
	@$(MAKE) deploy-bundle
	@$(MAKE) deploy-prerender

# Deploy the pre-rendered dialogue pack. Preserves the per-speaker folder
# layout (prerender/<speaker>/<sha>.ogg) — the C# PrerenderLookup reads the
# manifest's `ogg` field as a relative path, so the tree must stay intact.
# The variants/ folder is debug-only and excluded from the deploy.
deploy-prerender:
	@if [ -f prerender/manifest.json ]; then \
		mkdir -p "$(VGTTS_DIR)/prerender" ; \
		cp prerender/manifest.json "$(VGTTS_DIR)/prerender/" ; \
		if [ -f prerender/captain_name_templates.json ]; then \
			cp prerender/captain_name_templates.json "$(VGTTS_DIR)/prerender/" ; \
		fi ; \
		for d in prerender/*/ ; do \
			case "$$d" in prerender/variants/) continue ;; esac ; \
			cp -r "$$d" "$(VGTTS_DIR)/prerender/" ; \
		done ; \
		count=$$(find "$(VGTTS_DIR)/prerender/" -name "*.ogg" | wc -l) ; \
		echo "Deployed $$count prerendered OGGs to $(VGTTS_DIR)/prerender/" ; \
	else \
		echo "No prerender/manifest.json — skipping prerender deploy" ; \
	fi

# Deploy the Kokoro + sherpa-onnx bundle (required for live TTS fallback).
deploy-bundle:
	@if [ -d tools/kokoro ] && [ -d tools/sherpa ]; then \
		mkdir -p "$(VGTTS_DIR)/tools" ; \
		cp -r tools/kokoro tools/sherpa "$(VGTTS_DIR)/tools/" ; \
		echo "Deployed Kokoro + sherpa-onnx bundle to $(VGTTS_DIR)/tools/" ; \
	else \
		echo "tools/kokoro or tools/sherpa missing — run 'make download-kokoro'" ; \
	fi

# Sherpa-onnx-tts CLI + Kokoro v1.0 multi-lang model bundle (~400 MB extracted)
SHERPA_VERSION := 1.12.39
SHERPA_URL := https://github.com/k2-fsa/sherpa-onnx/releases/download/v$(SHERPA_VERSION)/sherpa-onnx-v$(SHERPA_VERSION)-win-x64-static-MT-Release.tar.bz2
KOKORO_URL := https://github.com/k2-fsa/sherpa-onnx/releases/download/tts-models/kokoro-multi-lang-v1_0.tar.bz2

download-kokoro:
	@mkdir -p tools/sherpa tools/kokoro /tmp/vgtts-dl
	@if [ ! -f tools/sherpa/sherpa-onnx-tts.exe ]; then \
		echo "Downloading sherpa-onnx CLI..." ; \
		curl -sSL -o /tmp/vgtts-dl/sherpa.tar.bz2 "$(SHERPA_URL)" ; \
		tar -xjf /tmp/vgtts-dl/sherpa.tar.bz2 -C /tmp/vgtts-dl \
			sherpa-onnx-v$(SHERPA_VERSION)-win-x64-static-MT-Release/bin/sherpa-onnx-offline-tts.exe ; \
		mv /tmp/vgtts-dl/sherpa-onnx-v$(SHERPA_VERSION)-win-x64-static-MT-Release/bin/sherpa-onnx-offline-tts.exe \
		   tools/sherpa/sherpa-onnx-tts.exe ; \
	fi
	@if [ ! -f tools/kokoro/model.onnx ]; then \
		echo "Downloading Kokoro v1.0 model..." ; \
		curl -sSL -o /tmp/vgtts-dl/kokoro.tar.bz2 "$(KOKORO_URL)" ; \
		tar -xjf /tmp/vgtts-dl/kokoro.tar.bz2 -C tools/kokoro --strip-components=1 ; \
	fi
	@echo "Kokoro bundle ready in tools/"

# Fetch both Piper and Kokoro — run once on a fresh clone.

# Build a distributable layout under dist/VGTTS-v<version>/ that a user can
# drop straight into their BepInEx/plugins folder. Zipping is left to the
# human so they can inspect the contents first.
PKG_VERSION := $(shell grep 'PluginVersion =' VGTTS/Plugin.cs | head -1 | sed 's/.*"\(.*\)".*/\1/')
PKG_NAME    := VGTTS-v$(PKG_VERSION)
PKG_DIR     := dist/$(PKG_NAME)

package: build
	@echo "Packaging $(PKG_NAME) into $(PKG_DIR)/"
	@rm -rf "$(PKG_DIR)"
	@# Nested VGTTS/ is intentional — it's the folder the user drops into
	@# BepInEx/plugins/. Inside it: DLL + prerender/ + tools/ side-by-side.
	@mkdir -p "$(PKG_DIR)/VGTTS/prerender" "$(PKG_DIR)/VGTTS/tools"
	@cp "$(BUILDDLL)" "$(PKG_DIR)/VGTTS/"
	@cp LICENSE THIRD_PARTY_NOTICES.md README.md "$(PKG_DIR)/VGTTS/" 2>/dev/null || true
	@# Prerender pack — preserve speaker-folder tree, skip variants/
	@cp prerender/manifest.json "$(PKG_DIR)/VGTTS/prerender/"
	@if [ -f prerender/captain_name_templates.json ]; then \
		cp prerender/captain_name_templates.json "$(PKG_DIR)/VGTTS/prerender/" ; \
	fi
	@for d in prerender/*/ ; do \
		case "$$d" in prerender/variants/) continue ;; esac ; \
		cp -r "$$d" "$(PKG_DIR)/VGTTS/prerender/" ; \
	done
	@# Kokoro + sherpa-onnx bundle
	@if [ -d tools/kokoro ] && [ -d tools/sherpa ]; then \
		cp -r tools/kokoro tools/sherpa "$(PKG_DIR)/VGTTS/tools/" ; \
	else \
		echo "tools/kokoro or tools/sherpa missing — run 'make download-kokoro' first" ; \
		exit 1 ; \
	fi
	@oggs=$$(find "$(PKG_DIR)/VGTTS/prerender/" -name "*.ogg" | wc -l) ; \
	 size=$$(du -sh "$(PKG_DIR)" | cut -f1) ; \
	 echo "Packaged $$oggs OGGs + DLL + Kokoro bundle — $$size at $(PKG_DIR)/"
	@echo "Zip it manually with: (cd $(PKG_DIR) && zip -qr ../$(PKG_NAME).zip VGTTS)"

clean:
	$(DOTNET) clean VGTTS/VGTTS.csproj
	rm -rf VGTTS/bin VGTTS/obj dist/
