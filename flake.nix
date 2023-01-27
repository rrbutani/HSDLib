{
  description = "nix flake for HSDRawViewer";
  inputs = {
    # nixpkgs_for_old_dot_net.url = github:NixOS/nixpkgs/nixos-21.11;
    # nixpkgs.url = github:NixOS/nixpkgs/nixos-22.11;

    # We want this change: https://github.com/NixOS/nixpkgs/commit/824d40aa0400e547c07c054ed7eddcf42b68955e
    #
    # Which hasn't made it into 22.11 yet.
    nixpkgs.url = github:NixOS/nixpkgs/nixos-unstable;
    flake-utils.url = github:numtide/flake-utils;
  };

  outputs = { self, nixpkgs, /* nixpkgs_for_old_dot_net, */ flake-utils }:
    # Only Linux (since we're using Wine), ~~arm64 and~~ x86_64:
    with flake-utils.lib; eachSystem (with system; [ aarch64-linux x86_64-linux ]) (sys: let
      np = nixpkgs.legacyPackages.${sys};
      # npOld = nixpkgs_for_old_dot_net.legacyPackages.${sys};

      winTarget = {
        # See: https://learn.microsoft.com/en-us/dotnet/core/rid-catalog#windows-rids
        # ${system.aarch64-linux} = "win-arm64"; # wine64 doesn't actually run on arm anyways..
        ${system.x86_64-linux} = "win-x64";
      }.${sys} or (throw "unsupported system ${sys}");
      hsdRawViewerPkg =
        { lib
        , stdenv
        , buildDotnetModule
        , dotnetPackages
        , dotnetCorePackages
        , msbuild
        }: buildDotnetModule rec {
          pname = "HSDRawViewer";
          version = "0.0.0";

          src = np.lib.cleanSource ./.;

          # run `nix run .#fetchDeps -- nuget-deps.nix` to update this
          nugetDeps = ./nuget-deps.nix;

          # The 6.0 SDK and runtime in nixpkgs is *too new* for Mono's MSBuild;
          # see: https://github.com/dotnet/core/issues/7701#issuecomment-1256679365
          #
          # We can't use `dotnet msbuild` because this app uses winforms (instead
          # of maui) which doesn't have a Linux implementation in .NET Core. Mono
          # kind of has an implementation.
          #
          # Using an older .net 6 SDK from an older nixpkgs is tricky (other
          # interface changes to `buildDotnetModule` and the SDK have happened in
          # the interim which we'd then have to paper over) and the Mono WinForms
          # implementation is supposed to be pretty buggy.
          #
          # It's also pretty apparent that upstream doesn't build/test for Linux
          # so: let's go another route and just build for windows (and try to run
          # under wine).
          # dotnet-sdk = npOld.dotnetCorePackages.sdk_6_0;
          # dotnet-runtime = npOld.dotnetCorePackages.runtime_6_0;

          dotnet-sdk = dotnetCorePackages.sdk_6_0;
          dotnet-runtime = dotnetCorePackages.runtime_6_0;

          buildType = "Release";
          selfContainedBuild = true;
          dotnetFlags = [ "/p:EnableWindowsTargeting=true" ];

          # We can't just stick this in `dotnetFlags` because then it'll get
          # picked up (duplicated) in the `fetch-deps` script's arguments too.
          #
          # `runtimeId` gets picked up by the default dotnet build and install
          # hooks but: we're not using the default build step (i.e. `dotnet build`)
          # dotnetBuildFlags = [ "-r" winTarget ];

          # Note: only available on `unstable` as of this writing:
          # https://github.com/NixOS/nixpkgs/pull/194276
          runtimeId = winTarget;

          # nativeBuildInputs = [ msbuild ]; # not using mono; don't need this
          buildPhase = ''
            runHook preBuild

            dotnet msbuild ''${dotnetFlags} /p:Configuration=Release ''${projectFile}

            runHook postBuild
          '';

          # The default dotenv install hook has `dotnet publish` run with
          # `--no-build` which seems to expect artifacts in different locations
          # (top-level instead of in subdir) than `msbuild` puts them.
          #
          # Running without `--no-build` works fine (but does seem to rebuild
          # some stuff); not sure what the correct way to do this is..
          #
          # Taken from: https://github.com/NixOS/nixpkgs/blob/20dcc6920f596846aac9787c46a31868f6ecab46/pkgs/build-support/dotnet/build-dotnet-module/hooks/dotnet-install-hook.sh#L19
          installPhase = ''
            runHook preInstall

            if [ "''${selfContainedBuild-}" ]; then
                dotnetInstallFlags+=(--runtime "$runtimeId" "--self-contained")
            else
                dotnetInstallFlags+=("--no-self-contained")
            fi

            if [ "''${useAppHost-}" ]; then
                dotnetInstallFlags+=("-p:UseAppHost=true")
            fi


            dotnet publish \
              -p:ContinuousIntegrationBuild=true \
              -p:Deterministic=true \
              --output "$out/lib/''${pname}" \
              --configuration "$buildType" \
              ''${dotnetInstallFlags[@]} \
              ''${dotnetFlags[@]}
              # --no-build

            runHook postInstall
          '';

          # projectFile = "HSDLib.sln";
        };
    in rec {
      packages = rec {
        hsdRawViewerWin = np.callPackage hsdRawViewerPkg {};
        fetchDeps = hsdRawViewerWin.fetch-deps;
        hsdRawViewer = np.writeScriptBin "hsdRawViewer" ''
          ${np.lib.getExe np.wine64} ${hsdRawViewerWin}/lib/HSDRawViewer/HSDRawViewer.exe
        '';
        default = hsdRawViewer;
      };

      apps = rec {
        fetchDeps = { type = "app"; program = "${packages.fetchDeps}"; };
        hsdRawViewer = { type = "app"; program = np.lib.getExe packages.hsdRawViewer; };
        default = hsdRawViewer;
      };
    });
}
