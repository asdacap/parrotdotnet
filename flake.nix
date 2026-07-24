{
  description = "Parrot Coder (.NET Native AOT)";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixpkgs-unstable";

  outputs = {
    self,
    nixpkgs,
  }: let
    systems = [
      "aarch64-darwin"
      "x86_64-darwin"
      "aarch64-linux"
      "x86_64-linux"
    ];
    forAllSystems = nixpkgs.lib.genAttrs systems;
  in {
    devShells = forAllSystems (
      system: let
        pkgs = nixpkgs.legacyPackages.${system};
        sdk = pkgs.dotnetCorePackages.sdk_10_0;

        # Native AOT shells out to a C toolchain and a linker. On NixOS these
        # are not on a fixed path, so the shell has to name them explicitly.
        aotToolchain = with pkgs;
          [
            clang
            lld
            zlib
          ]
          ++ lib.optionals stdenv.isLinux [stdenv.cc.libc];

        # The shipped binary is statically linked against musl, so it depends on
        # no interpreter and no shared library at all.
        muslToolchain = pkgs.pkgsCross.musl64.stdenv.cc;

        # ILCompiler treats a musl RID on a glibc host as a cross build and
        # passes clang's --target=. The nix musl wrapper is gcc, already targets
        # musl, and rejects the flag. Drop it and forward the rest.
        muslLinker = pkgs.writeShellScriptBin "musl-clang" ''
          args=()
          for a in "$@"; do
            case "$a" in
              --target=*) ;;
              *) args+=("$a") ;;
            esac
          done
          exec ${muslToolchain}/bin/x86_64-unknown-linux-musl-gcc "''${args[@]}"
        '';
      in {
        default = pkgs.mkShell {
          packages = with pkgs;
            [
              sdk
              icu
              git
              alejandra
              nil
            ]
            ++ aotToolchain
            ++ lib.optionals (system == "x86_64-linux") [muslToolchain muslLinker];

          env = {
            DOTNET_ROOT = "${sdk}/share/dotnet";
            DOTNET_CLI_TELEMETRY_OPTOUT = "1";
            DOTNET_NOLOGO = "1";
            # Native AOT emits an executable that must find its interpreter and
            # libs at runtime; keep the store paths on the link line.
            CppCompilerAndLinker = "clang";
          };

          shellHook = ''
            export PATH="$PWD/.dotnet/tools:$PATH"
          '';
        };
      }
    );

    # The .NET build is not a flake check: restoring NuGet packages needs
    # network access, which the Nix build sandbox denies. Vendoring the restore
    # would mean a hash to refresh on every dependency change, so the build,
    # test, and publish gates run in the dev shell and in CI instead. This check
    # covers only what can be verified hermetically.
    checks = forAllSystems (
      system: let
        pkgs = nixpkgs.legacyPackages.${system};
      in {
        nix-format =
          pkgs.runCommand "parrot-nix-format" {
            nativeBuildInputs = [pkgs.alejandra];
            src = ./.;
          } ''
            alejandra --check "$src/flake.nix"
            touch "$out"
          '';
      }
    );

    formatter = forAllSystems (system: nixpkgs.legacyPackages.${system}.alejandra);
  };
}
