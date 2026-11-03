<script setup lang="ts">
import QRCode from 'qrcode'
import { onMounted, ref } from 'vue'

// The code holds only the address; the app fetches everything else from /api/v1/config.
const address = location.origin
const qr = ref('')

onMounted(async () => {
  qr.value = await QRCode.toDataURL(address, { margin: 1, width: 240, errorCorrectionLevel: 'M' })
})
</script>

<template>
  <div class="card flex flex-col items-center gap-6 p-6 sm:flex-row sm:items-start">
    <img v-if="qr" :src="qr" alt="QR-Code mit der Server-Adresse" class="size-60 rounded-lg bg-white p-2" />
    <div class="text-sm text-stone-700 dark:text-stone-300">
      <h2 class="mb-2 text-base font-semibold text-stone-900 dark:text-white">iPhone einrichten</h2>
      <ol class="list-decimal space-y-1.5 pl-5">
        <li>Tailscale auf dem iPhone verbinden, am besten mit „On Demand“.</li>
        <li>Onkel Bummel öffnen und „QR-Code scannen“ wählen, oder die Adresse eintippen:</li>
      </ol>
      <code class="my-3 block rounded-lg bg-stone-100 px-3 py-2 font-mono text-xs select-all dark:bg-stone-800">{{ address }}</code>
      <p>Danach meldest du dich beim Identity-Server an, und die App registriert das Gerät für Pushes.</p>
    </div>
  </div>
</template>
