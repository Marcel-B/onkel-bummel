<script setup lang="ts">
import { LoaderCircle, LogOut, Menu, Search, Send, Settings, X } from 'lucide-vue-next'
import { computed, onMounted, ref, watch } from 'vue'
import { RouterLink, RouterView, useRoute, useRouter } from 'vue-router'
import TopicList from './components/TopicList.vue'
import { useAuthStore } from './stores/auth'
import { useLiveStore } from './stores/live'
import { useTopicsStore } from './stores/topics'

const auth = useAuthStore()
const topics = useTopicsStore()
const live = useLiveStore()
const route = useRoute()
const router = useRouter()

const menuOpen = ref(false)
const search = ref('')
const accessKey = ref('')
const signingIn = ref(false)

onMounted(() => auth.load())

// Right away when the cookie was there, or once the access key was entered.
watch(() => auth.state, async (state, before) => {
  if (state !== 'signedIn' || before === 'signedIn') return
  await topics.load()
  live.start()
})

async function submitKey() {
  signingIn.value = true
  try {
    if (await auth.signInWithKey(accessKey.value)) accessKey.value = ''
  } finally {
    signingIn.value = false
  }
}

watch(() => route.fullPath, () => {
  menuOpen.value = false
  search.value = typeof route.query.q === 'string' ? route.query.q : ''
}, { immediate: true })

// The tab title counts the unread messages, like the badge of the app on the phone.
watch(() => topics.unreadTotal, (unread) => {
  document.title = unread > 0 ? `(${unread}) Onkel Bummel` : 'Onkel Bummel'
})

/** The topic on screen, if any. */
const currentTopic = computed(() =>
  route.name === 'topic' || route.name === 'gallery' ? String(route.params.topic) : undefined,
)

/** Searches in the topic on screen, otherwise in all of them. */
function submitSearch() {
  const topic = currentTopic.value
  const query = { ...route.query, q: search.value.trim() || undefined }
  void router.push(topic ? { name: 'topic', params: { topic }, query } : { name: 'inbox', query })
}
</script>

<template>
  <div v-if="auth.state === 'loading'" class="flex h-dvh items-center justify-center text-stone-500">
    <LoaderCircle class="size-6 animate-spin" />
  </div>

  <div v-else-if="auth.state === 'needsKey'" class="flex h-dvh items-center justify-center p-6">
    <form class="card w-full max-w-sm p-6" @submit.prevent="submitKey">
      <img src="/favicon.svg" alt="" class="mx-auto mb-4 size-12" />
      <h1 class="mb-2 text-center text-lg font-semibold">Onkel Bummel</h1>
      <p class="mb-4 text-sm text-stone-600 dark:text-stone-400">
        Melde dich mit dem Zugangsschlüssel an. Der Server schreibt ihn beim ersten Start ins Log, einen neuen
        gibt es mit <code>--new-access-key</code>.
      </p>
      <label for="access-key" class="mb-1 block text-sm font-medium">Zugangsschlüssel</label>
      <input
        id="access-key"
        v-model="accessKey"
        type="password"
        autocomplete="current-password"
        placeholder="obk_…"
        required
        class="input mb-3 w-full font-mono"
      />
      <p v-if="auth.error" class="mb-3 text-sm text-red-600 dark:text-red-400" role="alert">{{ auth.error }}</p>
      <button class="btn-primary w-full justify-center" :disabled="signingIn || !accessKey.trim()">
        <LoaderCircle v-if="signingIn" class="size-4 animate-spin" />
        Anmelden
      </button>
    </form>
  </div>

  <div v-else-if="auth.state !== 'signedIn'" class="flex h-dvh items-center justify-center p-6">
    <div class="card max-w-sm p-6 text-center">
      <img src="/favicon.svg" alt="" class="mx-auto mb-4 size-12" />
      <h1 class="mb-2 text-lg font-semibold">Onkel Bummel</h1>
      <p v-if="auth.state === 'forbidden'" class="mb-4 text-sm text-stone-600 dark:text-stone-400">
        Du bist angemeldet, aber dir fehlt die Rolle <code>onkel-bummel</code>. Sie lässt sich im Admin des
        Identity-Servers zuweisen.
      </p>
      <p v-else class="mb-4 text-sm text-stone-600 dark:text-stone-400">{{ auth.error }}</p>
      <div class="flex justify-center gap-2">
        <a href="/auth/login" class="btn-primary">Anmelden</a>
        <form v-if="auth.state === 'forbidden'" method="post" action="/auth/logout">
          <button class="btn">Abmelden</button>
        </form>
      </div>
    </div>
  </div>

  <div v-else class="flex h-dvh flex-col">
    <header class="flex items-center gap-2 border-b border-stone-200 bg-white px-3 py-2 dark:border-stone-800 dark:bg-stone-900">
      <button class="btn-icon md:hidden" :aria-label="menuOpen ? 'Menü schließen' : 'Menü öffnen'" @click="menuOpen = !menuOpen">
        <X v-if="menuOpen" class="size-5" />
        <Menu v-else class="size-5" />
      </button>
      <RouterLink to="/" class="flex items-center gap-2 font-semibold">
        <img src="/favicon.svg" alt="" class="size-7" />
        <span class="hidden sm:inline">Onkel Bummel</span>
      </RouterLink>

      <form class="relative mx-2 max-w-md flex-1" role="search" @submit.prevent="submitSearch">
        <Search class="pointer-events-none absolute top-1/2 left-2.5 size-4 -translate-y-1/2 text-stone-400" />
        <input v-model="search" type="search" placeholder="Suchen …" aria-label="Nachrichten durchsuchen" class="input w-full pl-8" />
      </form>

      <span
        class="size-2 shrink-0 rounded-full"
        :class="live.connected ? 'bg-emerald-500' : 'bg-stone-400'"
        :title="live.connected ? 'Live verbunden' : 'Keine Live-Verbindung'"
      />
      <RouterLink
        :to="{ name: 'send', query: currentTopic ? { topic: currentTopic } : {} }"
        class="btn-icon"
        title="Nachricht senden"
        aria-label="Nachricht senden"
      >
        <Send class="size-5" />
      </RouterLink>
      <RouterLink to="/verwaltung" class="btn-icon" title="Verwaltung" aria-label="Verwaltung">
        <Settings class="size-5" />
      </RouterLink>
      <form method="post" action="/auth/logout">
        <button class="btn-icon" :title="`Abmelden (${auth.user?.name})`" aria-label="Abmelden">
          <LogOut class="size-5" />
        </button>
      </form>
    </header>

    <div class="relative flex min-h-0 flex-1">
      <aside
        class="absolute inset-y-0 left-0 z-20 w-72 overflow-y-auto border-r border-stone-200 bg-stone-50 p-2 transition-transform md:static md:translate-x-0 dark:border-stone-800 dark:bg-stone-950"
        :class="menuOpen ? 'translate-x-0 shadow-xl' : '-translate-x-full'"
      >
        <TopicList />
      </aside>
      <div v-if="menuOpen" class="absolute inset-0 z-10 bg-black/30 md:hidden" @click="menuOpen = false" />
      <main class="min-w-0 flex-1 overflow-y-auto" data-scroll-root>
        <RouterView />
      </main>
    </div>
  </div>
</template>
