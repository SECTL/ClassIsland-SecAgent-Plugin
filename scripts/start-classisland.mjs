import fs from "node:fs";
import path from "node:path";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

const pluginRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const envPath = path.join(pluginRoot, ".env");

function loadDotEnv(file) {
  if (!fs.existsSync(file)) return {};
  const values = {};
  for (const line of fs.readFileSync(file, "utf8").split(/\r?\n/)) {
    const match = line.match(/^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*?)\s*$/);
    if (!match || match[1].startsWith("#")) continue;
    values[match[1]] = match[2].replace(/^(['"])(.*)\1$/, "$2");
  }
  return values;
}

const fileEnv = loadDotEnv(envPath);
const executable = process.env.CLASSISLAND_EXECUTABLE || fileEnv.CLASSISLAND_EXECUTABLE;
if (!executable) {
  console.error(`未配置 CLASSISLAND_EXECUTABLE，请编辑 ${envPath}`);
  process.exit(1);
}
if (!fs.existsSync(executable)) {
  console.error(`ClassIsland 可执行文件不存在：${executable}`);
  process.exit(1);
}

const pluginOutput = process.env.PLUGIN_OUTPUT_DIRECTORY ||
  fileEnv.PLUGIN_OUTPUT_DIRECTORY ||
  path.join(pluginRoot, "bin", "Debug", "net8.0-windows");
const workingDirectory = process.env.CLASSISLAND_WORKING_DIRECTORY ||
  fileEnv.CLASSISLAND_WORKING_DIRECTORY ||
  path.dirname(executable);

if (!fs.existsSync(path.join(pluginOutput, "manifest.yml"))) {
  console.error(`插件输出目录中未找到 manifest.yml：${pluginOutput}`);
  console.error("请先执行 pnpm build");
  process.exit(1);
}

console.log(`启动 ClassIsland：${executable}`);
console.log(`加载插件目录：${pluginOutput}`);

const child = spawn(executable, ["-epp", pluginOutput], {
  cwd: workingDirectory,
  stdio: "inherit",
  env: process.env
});

child.on("error", (error) => {
  console.error(`启动 ClassIsland 失败：${error.message}`);
  process.exitCode = 1;
});

child.on("exit", (code, signal) => {
  if (signal) console.log(`ClassIsland 已被信号 ${signal} 终止`);
  else console.log(`ClassIsland 已退出，代码：${code ?? 0}`);
  process.exitCode = code ?? 1;
});
