import js from "@eslint/js";
import tseslint from "typescript-eslint";

export default [
    {
        ignores: ["dist/**", "node_modules/**"]
    },
    js.configs.recommended,
    ...tseslint.configs.recommended,
    {
        files: ["src/**/*.ts", "types/**/*.d.ts"],
        languageOptions: {
            ecmaVersion: 2020,
            sourceType: "module"
        },
        rules: {
            "@typescript-eslint/no-explicit-any": "off",
            "@typescript-eslint/no-unused-vars": [
                "error",
                {
                    argsIgnorePattern: "^_",
                    varsIgnorePattern: "^_"
                }
            ],
            "@typescript-eslint/no-empty-object-type": [
                "error",
                {
                    allowInterfaces: "always"
                }
            ]
        }
    }
];
