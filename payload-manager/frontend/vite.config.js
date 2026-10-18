import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import { viteSingleFile } from 'vite-plugin-singlefile'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react(), viteSingleFile()],
  build: {
    target: ['es2015', 'safari11'],
    minify: 'terser',
    terserOptions: {
      compress: {
        drop_console: false,
        drop_debugger: true,
      },
      mangle: {
        safari10: true,
      },
      format: {
        comments: false,
        safari10: true,
      },
    },
    cssCodeSplit: false,
    assetsInlineLimit: 100000, // Inline all assets
  },
  server: {
    proxy: {
      // Preserve the browser-facing Host so backend Origin/Referer checks
      // work through the same-origin development proxy.
      '/usb_move_check': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/usb_move_perform': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/processes_list': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/process_kill': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/history_list': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/shutdown': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/getip': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/version': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/list_payloads': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/autoload_status': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/get_config': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/set_config': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/loadpayload:': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/manage:': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/abort': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/autoload_clear': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/events': {
        target: 'http://127.0.0.1:8081',
        changeOrigin: false,
        sse: true
      },
      '/sources_list': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/sources_set': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/sources_add': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/sources_remove': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/repository_payloads': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/repository_refresh': { target: 'http://127.0.0.1:8081', changeOrigin: false },
      '/repository_install': { target: 'http://127.0.0.1:8081', changeOrigin: false }
    }
  }
})
