import { createRouter, createWebHistory } from 'vue-router'
import TimelineView from './views/TimelineView.vue'

// German paths, like the rest of the interface. The server answers every path outside /api with the app.
export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'inbox', component: TimelineView },
    { path: '/topics/:topic', name: 'topic', component: TimelineView, props: true },
    { path: '/topics/:topic/galerie', name: 'gallery', component: () => import('./views/GalleryView.vue'), props: true },
    { path: '/galerie', name: 'gallery-all', component: () => import('./views/GalleryView.vue') },
    {
      path: '/verwaltung',
      component: () => import('./views/admin/AdminView.vue'),
      children: [
        { path: '', redirect: { name: 'admin-topics' } },
        { path: 'topics', name: 'admin-topics', component: () => import('./views/admin/TopicsAdmin.vue') },
        { path: 'tokens', name: 'admin-tokens', component: () => import('./views/admin/TokensAdmin.vue') },
        { path: 'geraete', name: 'admin-devices', component: () => import('./views/admin/DevicesAdmin.vue') },
        { path: 'app', name: 'admin-app', component: () => import('./views/admin/AppSetup.vue') },
      ],
    },
    { path: '/:rest(.*)*', redirect: '/' },
  ],
})
